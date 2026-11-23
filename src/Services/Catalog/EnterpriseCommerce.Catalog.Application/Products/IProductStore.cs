using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.Application.Products;

/// <summary>
/// A merchant listing filter. Every merchant query is scoped to one tenant (ADR-014); <c>Text</c> is a sanitized
/// search text and <c>SkuKey</c> an exact SKU comparison key.
/// </summary>
public sealed record MerchantProductFilter(ProductStatus? Status, string? Text, string? SkuKey, CatalogSort Sort);

/// <summary>
/// Persistence of the product aggregate. Every read and write names the tenant, so a product of another tenant is
/// never found, whatever its ID. None of these methods authorizes anything by itself.
/// </summary>
public interface IProductStore
{
    Task<Product?> GetAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken);

    /// <summary>The product created by this subject in this tenant with this <c>Idempotency-Key</c>, if any.</summary>
    Task<Product?> FindByCreationKeyAsync(Guid tenantId, string subjectId, string idempotencyKey, CancellationToken cancellationToken);

    Task<WriteOutcome> InsertAsync(Product product, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the stored product if it still has <paramref name="expectedVersion"/>, and advances the version of the
    /// product (<see cref="Product.AcceptStoredVersion"/>) on success.
    /// </summary>
    Task<WriteOutcome> ReplaceAsync(Product product, long expectedVersion, CancellationToken cancellationToken);

    Task<Page<Product>> ListAsync(Guid tenantId, MerchantProductFilter filter, PageRequest page, CancellationToken cancellationToken);
}
