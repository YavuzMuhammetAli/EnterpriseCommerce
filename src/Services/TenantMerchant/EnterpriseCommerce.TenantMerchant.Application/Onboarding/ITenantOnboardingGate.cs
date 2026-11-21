using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.Application.Onboarding;

/// <summary>A self-service tenant found by its creator's idempotency key.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="RequestHash">The hash of the request that created it.</param>
/// <param name="CreatorMembership">The creator's current (not removed) membership of the tenant, if any.</param>
public sealed record SelfServiceTenant(TenantView Tenant, IReadOnlyList<byte> RequestHash, MembershipView? CreatorMembership);

/// <summary>
/// Runs the self-service onboarding of one Identity subject <b>serialized per subject</b>: one short transaction that
/// first takes an exclusive application lock on the subject, so that two requests of the same user never both decide
/// on the same idempotency key or the same tenant count. Other subjects are not blocked.
/// </summary>
/// <remarks>
/// The work reads, decides and stages the tenant, its owner membership and the audit record in the session; they are
/// saved and committed together only when the work returns <see cref="WriteDecision.Commit{TResult}"/>. A deadlock
/// retries the whole transaction in a fresh session; the work must therefore have no side effects outside it. An
/// exhausted retry, a lock timeout or an unavailable database throws <see cref="DataStoreUnavailableException"/>, and a
/// unique constraint (a slug another subject took meanwhile) throws <see cref="TenantWriteConflictException"/>.
/// </remarks>
public interface ITenantOnboardingGate
{
    Task<TResult> ExecuteAsync<TResult>(
        SubjectId subject,
        Func<ITenantOnboardingSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken);
}

/// <summary>The reads and changes of one subject's locked onboarding transaction. Not a generic repository.</summary>
public interface ITenantOnboardingSession
{
    /// <summary>The self-service tenant the locked subject created with this idempotency key, if any.</summary>
    Task<SelfServiceTenant?> FindByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>How many tenants the locked subject has created through self-service.</summary>
    Task<int> CountTenantsAsync(CancellationToken cancellationToken);

    /// <summary>Whether any tenant, of any origin, has the slug.</summary>
    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Stages a new self-service tenant of the locked subject, with its owner membership and audit record.</summary>
    void Add(Tenant tenant, Membership owner, MembershipAuditRecord audit);
}
