using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.Application.Common;

/// <summary>The authenticated merchant-portal user behind a request, and the request's correlation ID.</summary>
/// <param name="SubjectId">The validated <c>sub</c> of the access token. Never a request value.</param>
/// <param name="CorrelationId">The request's <c>X-Correlation-ID</c>, recorded in audit records.</param>
public sealed record MemberCaller(string SubjectId, string? CorrelationId);

/// <summary>The result of one write unit, and whether its changes are committed.</summary>
public readonly record struct WriteDecision<TResult>(bool ShouldCommit, TResult Result);

/// <summary>Factory methods for <see cref="WriteDecision{TResult}"/>.</summary>
public static class WriteDecision
{
    public static WriteDecision<TResult> Commit<TResult>(TResult result) => new(true, result);

    public static WriteDecision<TResult> Rollback<TResult>(TResult result) => new(false, result);
}

/// <summary>
/// Runs every membership and invitation change of one tenant <b>serialized per tenant</b> (ADR-021): one short
/// transaction that first takes an update lock on the tenant row, so that two changes of the same tenant never both
/// decide on the same state (for example two owners demoting each other). Other tenants and readers are not blocked.
/// </summary>
/// <remarks>
/// <para>
/// The work reads, decides and stages its changes, including the audit record, in the session; they are saved and
/// committed together only when the work returns <see cref="WriteDecision.Commit{TResult}"/>. Any failure rolls all
/// of it back, so an audit write that fails also fails the change.
/// </para>
/// <para>
/// A SQL Server deadlock causes a <b>complete</b> retry in a fresh session, with a bounded number of attempts; the work
/// may therefore run more than once and must have no side effects outside the session. An exhausted retry, a lock
/// timeout or an unavailable database throws <see cref="DataStoreUnavailableException"/>. A rejected write throws
/// <see cref="TenantWriteConflictException"/>. Nothing is ever reported as done when it was not committed.
/// </para>
/// </remarks>
public interface ITenantWriteGate
{
    /// <param name="operation">A stable operation name, for logs.</param>
    /// <param name="tenantId">The tenant whose changes are serialized.</param>
    /// <param name="work">The decision, run inside the locked transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Guid tenantId,
        Func<ITenantWriteSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken);
}

/// <summary>
/// The reads and changes available inside one locked tenant transaction. Every read is scoped to the locked tenant,
/// and every entity read is tracked: changing it stages the update. Not a generic repository.
/// </summary>
public interface ITenantWriteSession
{
    /// <summary>Whether the locked tenant exists. When it does not, nothing in it can be read or changed.</summary>
    bool TenantExists { get; }

    /// <summary>A membership of the tenant by ID, in any status.</summary>
    Task<Membership?> FindMembershipAsync(Guid membershipId, CancellationToken cancellationToken);

    /// <summary>The subject's current (not removed) membership of the tenant.</summary>
    Task<Membership?> FindCurrentMembershipAsync(string subjectId, CancellationToken cancellationToken);

    /// <summary>The tenant's number of active <see cref="MerchantRole.MerchantOwner"/> memberships.</summary>
    Task<int> CountActiveOwnersAsync(CancellationToken cancellationToken);

    /// <summary>An invitation of the tenant by ID.</summary>
    Task<MerchantInvitation?> FindInvitationAsync(Guid invitationId, CancellationToken cancellationToken);

    /// <summary>The tenant's pending invitation for a normalized address (expired or not), if any.</summary>
    Task<MerchantInvitation?> FindPendingInvitationAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>The tenant's number of pending invitations that are not past their expiry at <paramref name="now"/>.</summary>
    Task<int> CountOpenInvitationsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    void Add(Membership membership);

    void Add(MerchantInvitation invitation);

    /// <summary>Stages an audit record. Audit records are append-only: there is no way to change or delete one.</summary>
    void Add(MembershipAuditRecord auditRecord);

    /// <summary>
    /// Writes the staged changes inside the still-open transaction, before more are staged. Needed when a later change
    /// depends on an earlier one at the database (for example closing a pending invitation before a new one for the
    /// same address is inserted). Nothing is committed until the work returns.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken);
}

/// <summary>Why the database rejected a tenant write.</summary>
public enum TenantWriteConflict
{
    /// <summary>A unique constraint: for example a second current membership of one subject.</summary>
    UniqueConstraint,

    /// <summary>An optimistic concurrency token (<c>rowversion</c>) no longer matched.</summary>
    Concurrency,

    /// <summary>The database refused to leave the tenant without an active owner (the backstop trigger).</summary>
    LastActiveOwner,
}

/// <summary>The database rejected a tenant write. Nothing was committed.</summary>
public sealed class TenantWriteConflictException : Exception
{
    public TenantWriteConflictException()
    {
    }

    public TenantWriteConflictException(string message)
        : base(message)
    {
    }

    public TenantWriteConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TenantWriteConflictException(TenantWriteConflict conflict, string message, Exception innerException)
        : base(message, innerException) => Conflict = conflict;

    public TenantWriteConflict Conflict { get; }
}
