namespace EnterpriseCommerce.TenantMerchant.Domain.Memberships;

/// <summary>
/// The merchant roles of a tenant membership (PHASE_3_TENANT_MERCHANT.md section 3). They are owned and enforced by
/// Tenant &amp; Merchant and never appear in Identity access tokens (ADR-017 section 2).
/// </summary>
public enum MerchantRole
{
    /// <summary>Owns the merchant organization. Granted only by trusted tenant provisioning.</summary>
    MerchantOwner,

    /// <summary>Administers the merchant organization.</summary>
    MerchantAdmin,

    /// <summary>A merchant employee.</summary>
    MerchantStaff,
}

/// <summary>
/// What a membership can be used for, before any target-specific rule (ADR-021). The rules that depend on the target
/// (which role may be invited, which membership may be changed) are in <see cref="MembershipAdministrationPolicy"/>.
/// </summary>
public enum MerchantPermission
{
    /// <summary>Read the tenant and one's own membership.</summary>
    ViewTenant,

    /// <summary>List and read the tenant's memberships.</summary>
    ViewMemberships,

    /// <summary>List and read the tenant's invitations.</summary>
    ViewInvitations,

    /// <summary>Create and cancel invitations, subject to the invited role.</summary>
    ManageInvitations,

    /// <summary>Suspend, reactivate and remove memberships, subject to the target's role.</summary>
    ManageMemberships,

    /// <summary>Change the role of a membership.</summary>
    ChangeRoles,
}

/// <summary>The fixed mapping from merchant roles to permissions.</summary>
public static class MerchantRolePermissions
{
    public static bool Allows(MerchantRole role, MerchantPermission permission) => permission switch
    {
        MerchantPermission.ViewTenant => role is MerchantRole.MerchantOwner or MerchantRole.MerchantAdmin or MerchantRole.MerchantStaff,
        MerchantPermission.ViewMemberships
            or MerchantPermission.ViewInvitations
            or MerchantPermission.ManageInvitations
            or MerchantPermission.ManageMemberships => role is MerchantRole.MerchantOwner or MerchantRole.MerchantAdmin,
        MerchantPermission.ChangeRoles => role is MerchantRole.MerchantOwner,
        _ => false,
    };
}
