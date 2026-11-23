using EnterpriseCommerce.Catalog.Application.Common;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Catalog.Application.Authorization;

/// <summary>The access decision for a merchant request.</summary>
public enum CatalogAccess
{
    Allowed,

    /// <summary>Not an active member, or no such tenant: answered exactly like a missing tenant (<c>404</c>).</summary>
    NotVisible,

    /// <summary>An active member whose role lacks the permission (<c>403</c>).</summary>
    Forbidden,
}

/// <summary>
/// Authorizes a merchant request against Tenant &amp; Merchant's verdict (ADR-014 rule 4, ADR-025 section 2). The tenant
/// in the route is never proof: the caller's active membership and role are verified on every request, and the verdict
/// is never cached. Without a verdict, <see cref="MembershipUnavailableException"/> propagates and nothing is read or
/// written. Denials are logged as security events.
/// </summary>
public sealed partial class CatalogAccessAuthorizer(IMembershipVerifier verifier, ILogger<CatalogAccessAuthorizer> logger)
{
    public async Task<CatalogAccess> AuthorizeAsync(MerchantCaller caller, Guid tenantId, CatalogPermission permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        MembershipVerdict verdict;
        try
        {
            verdict = await verifier.VerifyAsync(tenantId, caller.SubjectId, caller.CorrelationId, cancellationToken);
        }
        catch (MembershipUnavailableException)
        {
            CatalogMetrics.RecordMembershipCheck("unavailable");
            throw;
        }

        if (!verdict.Active)
        {
            CatalogMetrics.RecordMembershipCheck("not_visible");
            LogNotVisible(logger, caller.SubjectId, tenantId, permission);
            return CatalogAccess.NotVisible;
        }

        if (!CatalogRolePermissions.IsKnownRole(verdict.Role))
        {
            LogUnknownRole(logger, tenantId);
        }

        if (!CatalogRolePermissions.Grants(verdict.Role, permission))
        {
            CatalogMetrics.RecordMembershipCheck("forbidden");
            LogForbidden(logger, caller.SubjectId, tenantId, permission);
            return CatalogAccess.Forbidden;
        }

        CatalogMetrics.RecordMembershipCheck("allowed");
        return CatalogAccess.Allowed;
    }

    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Warning,
        Message = "Catalog access denied: subject {Subject} has no active membership in tenant {TenantId} (permission {Permission})")]
    private static partial void LogNotVisible(ILogger logger, string subject, Guid tenantId, CatalogPermission permission);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Warning,
        Message = "Catalog access denied: the role of subject {Subject} in tenant {TenantId} lacks permission {Permission}")]
    private static partial void LogForbidden(ILogger logger, string subject, Guid tenantId, CatalogPermission permission);

    // The role value itself is not logged: it is untrusted input of an open set, and could be arbitrarily long.
    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Warning,
        Message = "Tenant & Merchant returned a role unknown to Catalog for tenant {TenantId}; it grants no permission")]
    private static partial void LogUnknownRole(ILogger logger, Guid tenantId);
}
