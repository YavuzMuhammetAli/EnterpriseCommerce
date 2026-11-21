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

/// <summary>What a membership can be used for. S1 defines only the read permissions its endpoints need.</summary>
public enum MerchantPermission
{
    ViewTenant,
    ViewMemberships,
}

/// <summary>The fixed mapping from merchant roles to permissions.</summary>
public static class MerchantRolePermissions
{
    public static bool Allows(MerchantRole role, MerchantPermission permission) => permission switch
    {
        MerchantPermission.ViewTenant => role is MerchantRole.MerchantOwner or MerchantRole.MerchantAdmin or MerchantRole.MerchantStaff,
        MerchantPermission.ViewMemberships => role is MerchantRole.MerchantOwner or MerchantRole.MerchantAdmin,
        _ => false,
    };
}
