using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.Application.Tenants;

/// <summary>A tenant with its merchant profile, as returned by the API.</summary>
public sealed record TenantView(Guid Id, string Slug, TenantStatus Status, DateTimeOffset CreatedAt, string DisplayName);

/// <summary>A membership, as returned by the API.</summary>
public sealed record MembershipView(
    Guid Id,
    Guid TenantId,
    string SubjectId,
    MerchantRole Role,
    MembershipStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset StatusChangedAt)
{
    public static MembershipView Of(Membership membership)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return new MembershipView(
            membership.Id,
            membership.TenantId,
            membership.SubjectId,
            membership.Role,
            membership.Status,
            membership.CreatedAt,
            membership.StatusChangedAt);
    }
}

/// <summary>One page of a tenant's memberships.</summary>
public sealed record MembershipPage(IReadOnlyList<MembershipView> Items, int Page, int PageSize, int TotalCount);

/// <summary>A tenant the caller is an active member of, with the caller's membership.</summary>
public sealed record CallerTenantView(TenantView Tenant, MembershipView Membership);

/// <summary>One page of the caller's tenants.</summary>
public sealed record CallerTenantPage(IReadOnlyList<CallerTenantView> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// Reads of tenant-owned data. Every membership query is scoped to one tenant (ADR-014); none of them authorizes
/// anything by itself (see <see cref="Authorization.TenantAccessAuthorizer"/>).
/// </summary>
public interface ITenantReadStore
{
    Task<TenantView?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The subject's current (not removed) membership in the tenant, if any.</summary>
    Task<MembershipView?> GetCurrentMembershipAsync(Guid tenantId, string subjectId, CancellationToken cancellationToken);

    /// <summary>The owner membership created by provisioning: the tenant's earliest <c>MerchantOwner</c> membership.</summary>
    Task<MembershipView?> GetInitialOwnerAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The tenant's current (not removed) memberships, oldest first.</summary>
    Task<MembershipPage> ListMembershipsAsync(Guid tenantId, int skip, int take, CancellationToken cancellationToken);

    /// <summary>A membership of the tenant by ID, in any status (including removed, which is kept as history).</summary>
    Task<MembershipView?> GetMembershipAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken);

    /// <summary>The tenants in which the subject has an <b>active</b> membership, oldest membership first.</summary>
    Task<CallerTenantPage> ListCallerTenantsAsync(string subjectId, int skip, int take, CancellationToken cancellationToken);
}
