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
    DateTimeOffset CreatedAt);

/// <summary>One page of a tenant's memberships.</summary>
public sealed record MembershipPage(IReadOnlyList<MembershipView> Items, int Page, int PageSize, int TotalCount);

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
}
