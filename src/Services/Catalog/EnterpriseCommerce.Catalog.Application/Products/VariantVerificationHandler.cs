namespace EnterpriseCommerce.Catalog.Application.Products;

/// <summary>
/// Catalog's verdict on a (tenant, product, variant) reference for another service (ADR-027 section 1). A missing
/// product, a product of another tenant and a missing variant all give the same negative verdict, which reveals nothing
/// about other tenants.
/// </summary>
/// <param name="Found">Whether a product with that ID in that tenant has a variant with that ID.</param>
/// <param name="ProductStatus">The product's lifecycle status (an open set for the caller), when found.</param>
/// <param name="VariantActive">Whether the variant is sellable, when found.</param>
/// <param name="Sku">The variant's SKU, when found.</param>
public sealed record VariantVerdict(bool Found, string? ProductStatus, bool? VariantActive, string? Sku)
{
    public static VariantVerdict NotFound { get; } = new(false, null, null, null);
}

/// <summary>
/// Answers the internal variant verification (ADR-027). Authorization is the endpoint's policy: an allow-listed service
/// client. The read is tenant-scoped, so another tenant's product is never found, whatever its ID.
/// </summary>
public sealed class VariantVerificationHandler(IProductStore products)
{
    public async Task<VariantVerdict> VerifyAsync(Guid tenantId, Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var product = await products.GetAsync(tenantId, productId, cancellationToken);
        if (product is null || product.TenantId != tenantId)
        {
            return VariantVerdict.NotFound;
        }

        var variant = product.Content.Variants.SingleOrDefault(candidate => candidate.Id == variantId);
        return variant is null
            ? VariantVerdict.NotFound
            : new VariantVerdict(true, product.Status.ToString(), variant.IsActive, variant.Sku.Value);
    }
}
