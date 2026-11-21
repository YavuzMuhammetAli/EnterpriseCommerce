using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.Application.Tenants;

/// <summary>A tenant as seen by one of its members, with the member's own membership.</summary>
public sealed record MemberTenantResult(TenantAccess Access, TenantView? Tenant, MembershipView? Membership);

/// <summary>Reads a tenant for an authenticated member (<see cref="MerchantPermission.ViewTenant"/>).</summary>
public sealed class GetTenantForMemberHandler(TenantAccessAuthorizer authorizer, ITenantReadStore store)
{
    public async Task<MemberTenantResult> HandleAsync(Guid tenantId, string subjectId, CancellationToken cancellationToken)
    {
        var decision = await authorizer.AuthorizeAsync(tenantId, subjectId, MerchantPermission.ViewTenant, cancellationToken);
        if (decision.Access != TenantAccess.Allowed)
        {
            return new MemberTenantResult(decision.Access, null, null);
        }

        var tenant = await store.GetTenantAsync(tenantId, cancellationToken);
        return tenant is null
            ? new MemberTenantResult(TenantAccess.NotVisible, null, null)
            : new MemberTenantResult(TenantAccess.Allowed, tenant, decision.Membership);
    }
}

public sealed record MembershipListResult(TenantAccess Access, MembershipPage? Page);

/// <summary>Lists a tenant's memberships for an authenticated member (<see cref="MerchantPermission.ViewMemberships"/>).</summary>
public sealed class ListMembershipsHandler(TenantAccessAuthorizer authorizer, ITenantReadStore store)
{
    public async Task<MembershipListResult> HandleAsync(
        Guid tenantId,
        string subjectId,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var decision = await authorizer.AuthorizeAsync(tenantId, subjectId, MerchantPermission.ViewMemberships, cancellationToken);
        if (decision.Access != TenantAccess.Allowed)
        {
            return new MembershipListResult(decision.Access, null);
        }

        var memberships = await store.ListMembershipsAsync(tenantId, page.Skip, page.PageSize, cancellationToken);
        return new MembershipListResult(TenantAccess.Allowed, memberships with { Page = page.Page });
    }
}

/// <summary>A tenant and its initial owner, as seen by the trusted provisioning caller.</summary>
public sealed record ServiceTenantResult(TenantView Tenant, MembershipView? InitialOwner);

/// <summary>
/// Reads a tenant for the allow-listed provisioning caller, for example to reconcile a provisioning request whose
/// outcome it did not receive.
/// </summary>
public sealed class GetTenantForServiceHandler(ITenantReadStore store)
{
    public async Task<ServiceTenantResult?> HandleAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await store.GetTenantAsync(tenantId, cancellationToken);
        return tenant is null
            ? null
            : new ServiceTenantResult(tenant, await store.GetInitialOwnerAsync(tenantId, cancellationToken));
    }
}
