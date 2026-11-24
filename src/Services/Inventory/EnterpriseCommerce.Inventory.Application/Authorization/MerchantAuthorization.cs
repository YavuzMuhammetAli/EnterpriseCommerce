using EnterpriseCommerce.Inventory.Application.Common;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Inventory.Application.Authorization;

/// <summary>What a merchant member may do in Inventory (ADR-026 section 10). Inventory's own permissions.</summary>
public enum InventoryPermission
{
    /// <summary>Read the tenant's items, balances, availability and movements.</summary>
    ViewInventory,

    /// <summary>Create inventory items and adjust stock.</summary>
    ManageInventory,
}

/// <summary>
/// The role-permission table. Roles are Tenant &amp; Merchant's role names, received as strings in its verdict and never
/// read from a token. An unknown role (an open set, for example a role added later) grants nothing. Staff is read-only:
/// changing stock changes what the marketplace can sell.
/// </summary>
public static class InventoryRolePermissions
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<InventoryPermission>> Table =
        new Dictionary<string, IReadOnlySet<InventoryPermission>>(StringComparer.Ordinal)
        {
            ["MerchantOwner"] = new HashSet<InventoryPermission> { InventoryPermission.ViewInventory, InventoryPermission.ManageInventory },
            ["MerchantAdmin"] = new HashSet<InventoryPermission> { InventoryPermission.ViewInventory, InventoryPermission.ManageInventory },
            ["MerchantStaff"] = new HashSet<InventoryPermission> { InventoryPermission.ViewInventory },
        };

    public static bool IsKnownRole(string? role) => role is not null && Table.ContainsKey(role);

    public static bool Grants(string? role, InventoryPermission permission) =>
        role is not null && Table.TryGetValue(role, out var permissions) && permissions.Contains(permission);
}

/// <summary>
/// Tenant &amp; Merchant's verdict for one subject in one tenant (ADR-025 section 1): an active membership and its role,
/// or nothing. The role is a string from Tenant &amp; Merchant's role set.
/// </summary>
public sealed record MembershipVerdict(bool Active, string? Role)
{
    public static readonly MembershipVerdict Inactive = new(false, null);
}

/// <summary>
/// Asks Tenant &amp; Merchant, the membership authority, for a verdict. Implementations never cache verdicts and throw
/// <see cref="MembershipUnavailableException"/> when no verdict can be obtained.
/// </summary>
public interface IMembershipVerifier
{
    Task<MembershipVerdict> VerifyAsync(Guid tenantId, string subjectId, string? correlationId, CancellationToken cancellationToken);
}

/// <summary>The authenticated merchant-portal user of a request: the token's subject and the request's correlation ID.</summary>
public sealed record MerchantCaller(string SubjectId, string CorrelationId);

/// <summary>
/// Tenant &amp; Merchant's membership verdict could not be obtained: no verification client is configured, the call
/// failed, timed out or answered something unexpected, or no service token was issued (ADR-025 section 2). The request
/// is refused without reading or writing anything, and the API answers <c>503</c>.
/// </summary>
public sealed class MembershipUnavailableException : Exception
{
    public MembershipUnavailableException()
    {
    }

    public MembershipUnavailableException(string message)
        : base(message)
    {
    }

    public MembershipUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The access decision for a merchant request.</summary>
public enum InventoryAccess
{
    Allowed,

    /// <summary>Not an active member, or no such tenant: answered exactly like a missing tenant (<c>404</c>).</summary>
    NotVisible,

    /// <summary>An active member whose role lacks the permission (<c>403</c>).</summary>
    Forbidden,
}

/// <summary>
/// Authorizes a merchant request against Tenant &amp; Merchant's verdict (ADR-014 rule 4, ADR-025 section 2, ADR-026
/// section 10). The tenant in the route is never proof: the caller's active membership and role are verified on every
/// request, and the verdict is never cached. Without a verdict, <see cref="MembershipUnavailableException"/> propagates
/// and nothing is read or written. Denials are logged as security events.
/// </summary>
public sealed partial class InventoryAccessAuthorizer(IMembershipVerifier verifier, ILogger<InventoryAccessAuthorizer> logger)
{
    public async Task<InventoryAccess> AuthorizeAsync(MerchantCaller caller, Guid tenantId, InventoryPermission permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);

        MembershipVerdict verdict;
        try
        {
            verdict = await verifier.VerifyAsync(tenantId, caller.SubjectId, caller.CorrelationId, cancellationToken);
        }
        catch (MembershipUnavailableException)
        {
            InventoryMetrics.RecordMembershipCheck("unavailable");
            throw;
        }

        if (!verdict.Active)
        {
            InventoryMetrics.RecordMembershipCheck("not_visible");
            LogNotVisible(logger, caller.SubjectId, tenantId, permission);
            return InventoryAccess.NotVisible;
        }

        if (!InventoryRolePermissions.IsKnownRole(verdict.Role))
        {
            LogUnknownRole(logger, tenantId);
        }

        if (!InventoryRolePermissions.Grants(verdict.Role, permission))
        {
            InventoryMetrics.RecordMembershipCheck("forbidden");
            LogForbidden(logger, caller.SubjectId, tenantId, permission);
            return InventoryAccess.Forbidden;
        }

        InventoryMetrics.RecordMembershipCheck("allowed");
        return InventoryAccess.Allowed;
    }

    /// <summary>The command status of a denied access.</summary>
    public static CommandStatus StatusOf(InventoryAccess access) =>
        access == InventoryAccess.Forbidden ? CommandStatus.PermissionDenied : CommandStatus.TenantNotVisible;

    [LoggerMessage(
        EventId = 5100,
        Level = LogLevel.Warning,
        Message = "Inventory access denied: subject {Subject} has no active membership in tenant {TenantId} (permission {Permission})")]
    private static partial void LogNotVisible(ILogger logger, string subject, Guid tenantId, InventoryPermission permission);

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Warning,
        Message = "Inventory access denied: the role of subject {Subject} in tenant {TenantId} lacks permission {Permission}")]
    private static partial void LogForbidden(ILogger logger, string subject, Guid tenantId, InventoryPermission permission);

    // The role value itself is not logged: it is untrusted input of an open set, and could be arbitrarily long.
    [LoggerMessage(
        EventId = 5102,
        Level = LogLevel.Warning,
        Message = "Tenant & Merchant returned a role unknown to Inventory for tenant {TenantId}; it grants no permission")]
    private static partial void LogUnknownRole(ILogger logger, Guid tenantId);
}
