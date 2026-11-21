using System.Text.Json.Serialization;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;

namespace EnterpriseCommerce.TenantMerchant.Api.Tenants;

/// <summary>
/// <c>POST /internal/v1/tenants</c> body. Unknown members are rejected with <c>400</c>, so a caller can never smuggle
/// a role, a membership list or any other field the contract does not define: the owner role is fixed by the server.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProvisionTenantRequest(string? Slug, string? DisplayName, string? OwnerSubjectId, string? ActorReference);

internal sealed record MerchantProfileResponse(string DisplayName);

internal sealed record TenantResponse(Guid Id, string Slug, string Status, DateTimeOffset CreatedAt, MerchantProfileResponse MerchantProfile);

internal sealed record MembershipResponse(Guid Id, string SubjectId, string Role, string Status, DateTimeOffset CreatedAt);

/// <summary>Provisioning result, and the internal tenant read: the tenant with its initial owner membership.</summary>
internal sealed record ProvisionedTenantResponse(
    Guid Id,
    string Slug,
    string Status,
    DateTimeOffset CreatedAt,
    MerchantProfileResponse MerchantProfile,
    MembershipResponse? OwnerMembership);

/// <summary>A tenant as seen by one of its members, with the caller's own membership.</summary>
internal sealed record MemberTenantResponse(
    Guid Id,
    string Slug,
    string Status,
    DateTimeOffset CreatedAt,
    MerchantProfileResponse MerchantProfile,
    MembershipResponse Membership);

internal sealed record MembershipPageResponse(IReadOnlyList<MembershipResponse> Items, int Page, int PageSize, int TotalCount);

internal static class TenantContractMapping
{
    public static ProvisionedTenantResponse ToProvisioned(TenantView tenant, MembershipView? owner) =>
        new(tenant.Id, tenant.Slug, tenant.Status.ToString(), tenant.CreatedAt, new MerchantProfileResponse(tenant.DisplayName), owner is null ? null : ToResponse(owner));

    public static MemberTenantResponse ToMember(TenantView tenant, MembershipView membership) =>
        new(tenant.Id, tenant.Slug, tenant.Status.ToString(), tenant.CreatedAt, new MerchantProfileResponse(tenant.DisplayName), ToResponse(membership));

    public static MembershipPageResponse ToResponse(MembershipPage page) =>
        new([.. page.Items.Select(ToResponse)], page.Page, page.PageSize, page.TotalCount);

    public static MembershipResponse ToResponse(MembershipView membership) =>
        new(membership.Id, membership.SubjectId, membership.Role.ToString(), membership.Status.ToString(), membership.CreatedAt);
}
