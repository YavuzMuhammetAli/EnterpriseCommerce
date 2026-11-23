namespace EnterpriseCommerce.Catalog.Application.Authorization;

/// <summary>What a merchant member may do in Catalog (ADR-025 section 2). Catalog's own permissions.</summary>
public enum CatalogPermission
{
    /// <summary>Read the tenant's products (any status) and brands.</summary>
    ViewCatalog,

    /// <summary>Create products and change their content, variants, attributes and media.</summary>
    EditProducts,

    /// <summary>Publish, unpublish, archive and restore products.</summary>
    PublishProducts,

    /// <summary>Create, change, retire and reactivate brands.</summary>
    ManageBrands,
}

/// <summary>
/// The role-permission table. Roles are Tenant &amp; Merchant's role names, received as strings in its verdict and never
/// read from a token. An unknown role (an open set, for example a role added later) grants nothing.
/// </summary>
public static class CatalogRolePermissions
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<CatalogPermission>> Table =
        new Dictionary<string, IReadOnlySet<CatalogPermission>>(StringComparer.Ordinal)
        {
            ["MerchantOwner"] = new HashSet<CatalogPermission> { CatalogPermission.ViewCatalog, CatalogPermission.EditProducts, CatalogPermission.PublishProducts, CatalogPermission.ManageBrands },
            ["MerchantAdmin"] = new HashSet<CatalogPermission> { CatalogPermission.ViewCatalog, CatalogPermission.EditProducts, CatalogPermission.PublishProducts, CatalogPermission.ManageBrands },
            ["MerchantStaff"] = new HashSet<CatalogPermission> { CatalogPermission.ViewCatalog, CatalogPermission.EditProducts },
        };

    public static bool IsKnownRole(string? role) => role is not null && Table.ContainsKey(role);

    public static bool Grants(string? role, CatalogPermission permission) =>
        role is not null && Table.TryGetValue(role, out var permissions) && permissions.Contains(permission);
}
