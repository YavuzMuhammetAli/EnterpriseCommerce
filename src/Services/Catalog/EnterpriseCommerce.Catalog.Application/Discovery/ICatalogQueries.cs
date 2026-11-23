using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.Application.Discovery;

/// <summary>An attribute filter: the key and the lower-case value key (ADR-024 section 9).</summary>
public sealed record AttributeFilter(string Key, string ValueKey);

/// <summary>A public discovery filter. Every field is validated and bounded before it reaches the store.</summary>
public sealed record PublicProductFilter(
    string? Text,
    Guid? CategoryId,
    Guid? BrandId,
    Guid? TenantId,
    IReadOnlyList<AttributeFilter> Attributes,
    CatalogSort Sort);

/// <summary>
/// The read side of public discovery (selective CQRS, ADR-009 and ADR-024 section 12): separate queries with their own
/// filters and indexes. Only <see cref="ProductStatus.Published"/> products are ever returned.
/// </summary>
public interface ICatalogQueries
{
    Task<Page<Product>> ListPublishedAsync(PublicProductFilter filter, PageRequest page, CancellationToken cancellationToken);

    Task<Product?> GetPublishedAsync(Guid productId, CancellationToken cancellationToken);

    Task<Product?> GetPublishedBySlugAsync(Guid tenantId, string slug, CancellationToken cancellationToken);
}
