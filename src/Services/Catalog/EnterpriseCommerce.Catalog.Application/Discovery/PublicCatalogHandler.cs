using EnterpriseCommerce.Catalog.Application.Brands;
using EnterpriseCommerce.Catalog.Application.Categories;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Brands;
using EnterpriseCommerce.Catalog.Domain.Categories;
using EnterpriseCommerce.Catalog.Domain.Common;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.Application.Discovery;

/// <summary>
/// A published product as the public sees it (ADR-024 section 9): only active variants, only active categories, the
/// brand only if active, and none of the merchant's internal data (status, version, audit subjects, idempotency).
/// </summary>
public sealed record PublicProductView(
    Product Product,
    Brand? Brand,
    IReadOnlyList<Category> Categories,
    IReadOnlyList<ProductVariant> Variants);

/// <summary>
/// Public discovery: anonymous reads of published products. The queries return <c>Published</c> products only; this
/// handler resolves brands and categories with one lookup each per page, and drops whatever is not active.
/// </summary>
public sealed class PublicCatalogHandler(ICatalogQueries queries, IBrandStore brands, ICategoryStore categories)
{
    public async Task<Page<PublicProductView>> ListAsync(PublicProductFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        var products = await queries.ListPublishedAsync(filter, page, cancellationToken);
        var views = await ViewsAsync(products.Items, cancellationToken);
        return new Page<PublicProductView>(views, products.PageNumber, products.PageSize, products.TotalCount);
    }

    public async Task<PublicProductView?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        await queries.GetPublishedAsync(productId, cancellationToken) is { } product
            ? (await ViewsAsync([product], cancellationToken))[0]
            : null;

    public async Task<PublicProductView?> LookupAsync(Guid tenantId, string slug, CancellationToken cancellationToken) =>
        await queries.GetPublishedBySlugAsync(tenantId, slug, cancellationToken) is { } product
            ? (await ViewsAsync([product], cancellationToken))[0]
            : null;

    private async Task<IReadOnlyList<PublicProductView>> ViewsAsync(IReadOnlyList<Product> products, CancellationToken cancellationToken)
    {
        var brandIds = products.Select(product => product.Content.BrandId).OfType<Guid>().Distinct().ToList();
        var categoryIds = products.SelectMany(product => product.Content.CategoryIds).Distinct().ToList();
        var activeBrands = (await brands.GetManyAsync(brandIds, cancellationToken))
            .Where(brand => brand.Status == CatalogStatus.Active)
            .ToDictionary(brand => brand.Id);
        var activeCategories = (await categories.GetManyAsync(categoryIds, cancellationToken))
            .Where(category => category.Status == CatalogStatus.Active)
            .ToDictionary(category => category.Id);

        return [.. products.Select(product => new PublicProductView(
            product,
            product.Content.BrandId is { } brandId && activeBrands.TryGetValue(brandId, out var brand) && brand.TenantId == product.TenantId ? brand : null,
            [.. product.Content.CategoryIds.Where(activeCategories.ContainsKey).Select(id => activeCategories[id])],
            [.. product.Content.Variants.Where(variant => variant.IsActive)]))];
    }
}
