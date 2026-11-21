using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.TenantMerchant.Application.Authorization;

public enum TenantAccess
{
    /// <summary>
    /// The subject has no active membership in the tenant. The API answers <c>404</c>, exactly as for a tenant that
    /// does not exist, so that tenant IDs cannot be probed.
    /// </summary>
    NotVisible,

    /// <summary>An active member whose role lacks the permission. The API answers <c>403</c>.</summary>
    Forbidden,

    Allowed,
}

public sealed record TenantAccessDecision(TenantAccess Access, MembershipView? Membership);

/// <summary>
/// Decides whether an authenticated Identity subject may use a permission in a tenant, from Tenant &amp; Merchant's own
/// membership data only (ADR-014). The tenant ID comes from the request and is never trusted by itself; tokens carry no
/// tenant or role claims (ADR-017 section 2), and none would be accepted as proof.
/// </summary>
public sealed partial class TenantAccessAuthorizer(ITenantReadStore store, ILogger<TenantAccessAuthorizer> logger)
{
    public async Task<TenantAccessDecision> AuthorizeAsync(
        Guid tenantId,
        string subjectId,
        MerchantPermission permission,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);

        var membership = await store.GetCurrentMembershipAsync(tenantId, subjectId, cancellationToken);
        if (membership is not { Status: MembershipStatus.Active })
        {
            // Threat T20 (PHASE_2_2_AUTHENTICATION_FLOWS.md): a cross-tenant or stale-access attempt.
            LogTenantNotVisible(logger, subjectId, tenantId, permission);
            return new TenantAccessDecision(TenantAccess.NotVisible, null);
        }

        if (!MerchantRolePermissions.Allows(membership.Role, permission))
        {
            LogPermissionDenied(logger, subjectId, tenantId, permission, membership.Role);
            return new TenantAccessDecision(TenantAccess.Forbidden, membership);
        }

        return new TenantAccessDecision(TenantAccess.Allowed, membership);
    }

    [LoggerMessage(
        EventId = 3100,
        Level = LogLevel.Warning,
        Message = "Tenant access denied: subject {Subject} has no active membership in tenant {TenantId} (permission {Permission})")]
    private static partial void LogTenantNotVisible(ILogger logger, string subject, Guid tenantId, MerchantPermission permission);

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "Tenant access denied: subject {Subject} with role {Role} lacks permission {Permission} in tenant {TenantId}")]
    private static partial void LogPermissionDenied(ILogger logger, string subject, Guid tenantId, MerchantPermission permission, MerchantRole role);
}
