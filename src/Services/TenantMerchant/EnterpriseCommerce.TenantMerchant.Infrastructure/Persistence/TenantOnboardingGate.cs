using System.Data;
using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Onboarding;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementation of <see cref="ITenantOnboardingGate"/>: per attempt, a fresh scope and context, one
/// <c>READ COMMITTED</c> transaction, and as its first statement an exclusive application lock on the subject, owned
/// by the transaction and so held until it commits or rolls back:
/// <code>sp_getapplock @Resource = N'tenant-onboarding:{SHA-256 of the subject}', @LockMode = 'Exclusive', @LockOwner = 'Transaction'</code>
/// </summary>
/// <remarks>
/// <para>
/// A new tenant has no row to lock yet, so the tenant row lock of <see cref="TenantWriteGate"/> cannot serialize its
/// creation; the subject is the unit whose decisions (the idempotency key and the per-subject limit) must not race.
/// Each statement after the lock is granted starts after the previous holder committed, so the count and the key
/// lookup are never stale, with or without <c>READ_COMMITTED_SNAPSHOT</c>.
/// </para>
/// <para>
/// The resource name is a hash, so that no subject value, whatever its characters or length, shapes it. Lock waits are
/// bounded (<see cref="LockTimeoutMilliseconds"/>): a lock that is not granted in time fails the request with
/// <c>503</c>.
/// </para>
/// </remarks>
internal sealed partial class TenantOnboardingGate(IServiceScopeFactory scopes, ILogger<TenantOnboardingGate> logger) : ITenantOnboardingGate
{
    public const int MaxAttempts = 3;
    public const int LockTimeoutMilliseconds = 5000;
    public const string LockResourcePrefix = "tenant-onboarding:";

    private const string SetLockTimeout = "SET LOCK_TIMEOUT 5000";

    /// <summary>The <c>sp_getapplock</c> result of a deadlock victim.</summary>
    private const int LockDeadlockVictim = -3;

    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(25);

    /// <summary>The application lock resource of a subject.</summary>
    public static string LockResource(SubjectId subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return LockResourcePrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject.Value)));
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        SubjectId subject,
        Func<ITenantOnboardingSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(work);

        for (var attempt = 1; ; attempt++)
        {
            Attempt<TResult> outcome;
            try
            {
                outcome = await RunOnceAsync(subject, work, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && SqlServerErrors.IsDeadlock(exception) && attempt < MaxAttempts)
            {
                outcome = Attempt<TResult>.Deadlocked;
            }
            catch (DbUpdateException exception) when (SqlServerErrors.IsUniqueViolation(exception))
            {
                throw new TenantWriteConflictException(TenantWriteConflict.UniqueConstraint, "A unique constraint rejected the new tenant.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && SqlServerErrors.IsSqlServerFailure(exception))
            {
                if (SqlServerErrors.IsDeadlock(exception))
                {
                    LogDeadlockExhausted(logger, subject.Value, attempt);
                }

                throw new DataStoreUnavailableException("The Tenant & Merchant database could not complete the self-service onboarding.", exception);
            }

            if (!outcome.IsDeadlock)
            {
                return outcome.Result!;
            }

            if (attempt >= MaxAttempts)
            {
                LogDeadlockExhausted(logger, subject.Value, attempt);
                throw new DataStoreUnavailableException("The Tenant & Merchant database could not complete the self-service onboarding.");
            }

            LogDeadlockRetried(logger, subject.Value, attempt);
            await Task.Delay(BaseBackoff * Math.Pow(2, attempt - 1) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 25)), cancellationToken);
        }
    }

    private async Task<Attempt<TResult>> RunOnceAsync<TResult>(
        SubjectId subject,
        Func<ITenantOnboardingSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TenantMerchantDbContext>();

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(SetLockTimeout, cancellationToken);

        // 0: granted at once; 1: granted after waiting. -1: timeout; -2: cancelled; -3: deadlock victim; -999: error.
        var lockResult = await AcquireLockAsync(context, transaction, LockResource(subject), cancellationToken);
        if (lockResult == LockDeadlockVictim)
        {
            return Attempt<TResult>.Deadlocked;
        }

        if (lockResult < 0)
        {
            LogLockNotGranted(logger, subject.Value, lockResult);
            throw new DataStoreUnavailableException($"The onboarding lock of the subject was not granted (sp_getapplock {lockResult}).");
        }

        var session = new TenantOnboardingSession(context, subject.Value);
        var decision = await work(session, cancellationToken);
        if (decision.ShouldCommit)
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return new Attempt<TResult>(false, decision.Result);
    }

    private static async Task<int> AcquireLockAsync(TenantMerchantDbContext context, IDbContextTransaction transaction, string resource, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "sp_getapplock";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = resource });
        command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = "Exclusive" });
        command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Transaction" });
        command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = LockTimeoutMilliseconds });
        var result = new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        command.Parameters.Add(result);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return (int)result.Value!;
    }

    [LoggerMessage(
        EventId = 3240,
        Level = LogLevel.Warning,
        Message = "Self-service onboarding of subject {Subject} was a deadlock victim on attempt {Attempt}; retrying the whole transaction")]
    private static partial void LogDeadlockRetried(ILogger logger, string subject, int attempt);

    [LoggerMessage(
        EventId = 3241,
        Level = LogLevel.Error,
        Message = "Self-service onboarding of subject {Subject} failed: deadlock retries exhausted after {Attempt} attempts")]
    private static partial void LogDeadlockExhausted(ILogger logger, string subject, int attempt);

    [LoggerMessage(
        EventId = 3242,
        Level = LogLevel.Warning,
        Message = "Self-service onboarding of subject {Subject} failed: the onboarding lock was not granted (sp_getapplock {ResultCode})")]
    private static partial void LogLockNotGranted(ILogger logger, string subject, int resultCode);

    /// <summary>One attempt: its result, or a deadlock that retries the whole transaction.</summary>
    private readonly record struct Attempt<TResult>(bool IsDeadlock, TResult? Result)
    {
        public static Attempt<TResult> Deadlocked => new(true, default);
    }
}

/// <summary>The reads and staged changes of one subject's locked onboarding transaction. Every query is scoped to the subject.</summary>
internal sealed class TenantOnboardingSession(TenantMerchantDbContext context, string subjectId) : ITenantOnboardingSession
{
    public async Task<SelfServiceTenant?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        var row = await context.Tenants
            .AsNoTracking()
            .Where(t => t.Origin == TenantOrigin.SelfService && t.SelfServiceSubjectId == subjectId && t.ProvisioningKey == idempotencyKey)
            .Select(t => new { Tenant = new TenantView(t.Id, t.Slug, t.Status, t.CreatedAt, t.MerchantProfile.DisplayName), t.ProvisioningRequestHash })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var membership = await context.Memberships
            .AsNoTracking()
            .Where(m => m.TenantId == row.Tenant.Id && m.SubjectId == subjectId && m.Status != MembershipStatus.Removed)
            .Select(m => new MembershipView(m.Id, m.TenantId, m.SubjectId, m.Role, m.Status, m.CreatedAt, m.StatusChangedAt))
            .SingleOrDefaultAsync(cancellationToken);
        return new SelfServiceTenant(row.Tenant, row.ProvisioningRequestHash, membership);
    }

    public Task<int> CountTenantsAsync(CancellationToken cancellationToken) =>
        context.Tenants.CountAsync(t => t.Origin == TenantOrigin.SelfService && t.SelfServiceSubjectId == subjectId, cancellationToken);

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
        context.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken);

    public void Add(Tenant tenant, Membership owner, MembershipAuditRecord audit)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(audit);

        // A session creates its own subject's tenant only; anything else is a programming error, never a silent write.
        if (tenant.Origin != TenantOrigin.SelfService
            || !string.Equals(tenant.SelfServiceSubjectId, subjectId, StringComparison.Ordinal)
            || owner.TenantId != tenant.Id
            || !string.Equals(owner.SubjectId, subjectId, StringComparison.Ordinal)
            || owner.Role != MerchantRole.MerchantOwner
            || audit.TenantId != tenant.Id)
        {
            throw new InvalidOperationException("An onboarding session only creates a self-service tenant owned by its own subject.");
        }

        context.Tenants.Add(tenant);
        context.Memberships.Add(owner);
        context.MembershipAuditRecords.Add(audit);
    }
}
