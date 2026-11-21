using System.Data;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementation of <see cref="ITenantWriteGate"/> (ADR-021): per attempt, a fresh scope and context, one
/// <c>READ COMMITTED</c> transaction, and as its first statement an update lock on the tenant row held to the end:
/// <code>SELECT 1 FROM [Tenants] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [Id] = @tenantId</code>
/// </summary>
/// <remarks>
/// <para>
/// Update locks conflict with each other but not with shared locks, so two writers of one tenant are serialized while
/// readers of the tenant, and writers of every other tenant, continue. Every membership and invitation change takes
/// the same lock first; the lock order is therefore always the same and changes of one tenant do not deadlock each
/// other. A deadlock with another statement is still possible and is retried as a whole (ADR-017 section 8).
/// </para>
/// <para>
/// Lock waits are bounded (<see cref="LockTimeoutMilliseconds"/>): a lock that cannot be obtained in time fails the
/// operation with <c>503</c> instead of holding the request.
/// </para>
/// </remarks>
internal sealed partial class TenantWriteGate(IServiceScopeFactory scopes, ILogger<TenantWriteGate> logger) : ITenantWriteGate
{
    public const int MaxAttempts = 3;
    public const int LockTimeoutMilliseconds = 5000;

    private const string SetLockTimeout = "SET LOCK_TIMEOUT 5000";

    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(25);

    public async Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Guid tenantId,
        Func<ITenantWriteSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(tenantId, work, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && SqlServerErrors.IsDeadlock(exception) && attempt < MaxAttempts)
            {
                LogDeadlockRetried(logger, operation, tenantId, attempt);
                await Task.Delay(BaseBackoff * Math.Pow(2, attempt - 1) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25)), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && SqlServerErrors.IsLastActiveOwnerViolation(exception))
            {
                throw new TenantWriteConflictException(TenantWriteConflict.LastActiveOwner, "The tenant must keep an active owner.", exception);
            }
            catch (DbUpdateConcurrencyException exception)
            {
                throw new TenantWriteConflictException(TenantWriteConflict.Concurrency, "A concurrent change won.", exception);
            }
            catch (DbUpdateException exception) when (SqlServerErrors.IsUniqueViolation(exception))
            {
                throw new TenantWriteConflictException(TenantWriteConflict.UniqueConstraint, "A unique constraint rejected the change.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && SqlServerErrors.IsSqlServerFailure(exception))
            {
                if (SqlServerErrors.IsDeadlock(exception))
                {
                    LogDeadlockExhausted(logger, operation, tenantId, attempt);
                }

                throw new DataStoreUnavailableException($"The Tenant & Merchant database could not complete '{operation}'.", exception);
            }
        }
    }

    private async Task<TResult> RunOnceAsync<TResult>(
        Guid tenantId,
        Func<ITenantWriteSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TenantMerchantDbContext>();

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(SetLockTimeout, cancellationToken);
        var locked = await context.Database
            .SqlQuery<int>($"SELECT 1 AS [Value] FROM [Tenants] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [Id] = {tenantId}")
            .ToListAsync(cancellationToken);

        var session = new TenantWriteSession(context, tenantId, locked.Count == 1);
        var decision = await work(session, cancellationToken);
        if (decision.ShouldCommit && session.TenantExists)
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return decision.Result;
    }

    [LoggerMessage(
        EventId = 3220,
        Level = LogLevel.Warning,
        Message = "Tenant write {Operation} for tenant {TenantId} was a deadlock victim on attempt {Attempt}; retrying the whole transaction")]
    private static partial void LogDeadlockRetried(ILogger logger, string operation, Guid tenantId, int attempt);

    [LoggerMessage(
        EventId = 3221,
        Level = LogLevel.Error,
        Message = "Tenant write {Operation} for tenant {TenantId} failed: deadlock retries exhausted after {Attempt} attempts")]
    private static partial void LogDeadlockExhausted(ILogger logger, string operation, Guid tenantId, int attempt);
}

/// <summary>The reads and staged changes of one locked tenant transaction. Every query is scoped to the locked tenant.</summary>
internal sealed class TenantWriteSession(TenantMerchantDbContext context, Guid tenantId, bool tenantExists) : ITenantWriteSession
{
    public bool TenantExists { get; } = tenantExists;

    public Task<Membership?> FindMembershipAsync(Guid membershipId, CancellationToken cancellationToken) =>
        TenantExists
            ? context.Memberships.SingleOrDefaultAsync(m => m.TenantId == tenantId && m.Id == membershipId, cancellationToken)
            : Task.FromResult<Membership?>(null);

    public Task<Membership?> FindCurrentMembershipAsync(string subjectId, CancellationToken cancellationToken) =>
        TenantExists
            ? context.Memberships.SingleOrDefaultAsync(
                m => m.TenantId == tenantId && m.SubjectId == subjectId && m.Status != MembershipStatus.Removed,
                cancellationToken)
            : Task.FromResult<Membership?>(null);

    public Task<int> CountActiveOwnersAsync(CancellationToken cancellationToken) =>
        context.Memberships.CountAsync(
            m => m.TenantId == tenantId && m.Role == MerchantRole.MerchantOwner && m.Status == MembershipStatus.Active,
            cancellationToken);

    public Task<MerchantInvitation?> FindInvitationAsync(Guid invitationId, CancellationToken cancellationToken) =>
        TenantExists
            ? context.MerchantInvitations.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == invitationId, cancellationToken)
            : Task.FromResult<MerchantInvitation?>(null);

    public Task<MerchantInvitation?> FindPendingInvitationAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        TenantExists
            ? context.MerchantInvitations.SingleOrDefaultAsync(
                i => i.TenantId == tenantId && i.NormalizedEmail == normalizedEmail && i.Status == InvitationStatus.Pending,
                cancellationToken)
            : Task.FromResult<MerchantInvitation?>(null);

    public Task<int> CountOpenInvitationsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        context.MerchantInvitations.CountAsync(
            i => i.TenantId == tenantId && i.Status == InvitationStatus.Pending && i.ExpiresAt > now,
            cancellationToken);

    public void Add(Membership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        EnsureInTenant(membership.TenantId);
        context.Memberships.Add(membership);
    }

    public void Add(MerchantInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        EnsureInTenant(invitation.TenantId);
        context.MerchantInvitations.Add(invitation);
    }

    public void Add(MembershipAuditRecord auditRecord)
    {
        ArgumentNullException.ThrowIfNull(auditRecord);
        EnsureInTenant(auditRecord.TenantId);
        context.MembershipAuditRecords.Add(auditRecord);
    }

    public Task FlushAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);

    /// <summary>A session changes its own tenant only; anything else is a programming error, never a silent cross-tenant write.</summary>
    private void EnsureInTenant(Guid entityTenantId)
    {
        if (!TenantExists || entityTenantId != tenantId)
        {
            throw new InvalidOperationException("A tenant write session only changes its own, existing tenant.");
        }
    }
}
