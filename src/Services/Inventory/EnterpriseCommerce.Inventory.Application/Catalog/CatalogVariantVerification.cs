namespace EnterpriseCommerce.Inventory.Application.Catalog;

/// <summary>
/// Catalog's verdict on a (tenant, product, variant) reference (ADR-027 section 1): whether the variant belongs to a
/// product of the tenant, the product's status (an open set), the variant's sellability and its SKU.
/// </summary>
public sealed record CatalogVariantVerdict(bool Found, string? ProductStatus, bool? VariantActive, string? Sku)
{
    public static readonly CatalogVariantVerdict NotFound = new(false, null, null, null);

    /// <summary>The statuses in which a variant may get an inventory item (ADR-027 section 2). Anything else fails closed.</summary>
    public static readonly IReadOnlySet<string> EligibleStatuses = new HashSet<string>(StringComparer.Ordinal) { "Draft", "Published" };

    public bool IsEligible => Found && ProductStatus is not null && EligibleStatuses.Contains(ProductStatus);
}

/// <summary>
/// Asks Catalog, the owner of products and variants, for a verdict (ADR-027). Implementations never cache verdicts and
/// throw <see cref="CatalogUnavailableException"/> when no verdict can be obtained.
/// </summary>
public interface ICatalogVariantVerifier
{
    Task<CatalogVariantVerdict> VerifyAsync(Guid tenantId, Guid productId, Guid variantId, string? correlationId, CancellationToken cancellationToken);
}

/// <summary>
/// Catalog's verdict could not be obtained: no client is configured, the call failed, timed out or answered something
/// unexpected, or no service token was issued (ADR-027 section 2). Nothing is written, and the API answers <c>503</c>.
/// </summary>
public sealed class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException()
    {
    }

    public CatalogUnavailableException(string message)
        : base(message)
    {
    }

    public CatalogUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
