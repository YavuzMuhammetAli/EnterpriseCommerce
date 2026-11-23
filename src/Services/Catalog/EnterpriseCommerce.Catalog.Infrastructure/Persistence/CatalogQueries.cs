using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Application.Discovery;
using EnterpriseCommerce.Catalog.Domain.Products;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// Public discovery queries (ADR-024 section 9). Every query starts with <c>status = Published</c>, so a draft or an
/// archived product is never returned. Filters are built from validated values only; no client expression reaches
/// MongoDB.
/// </summary>
internal sealed class CatalogQueries(CatalogMongoContext context) : ICatalogQueries
{
    private static readonly FilterDefinitionBuilder<ProductDocument> Filter = Builders<ProductDocument>.Filter;
    private static readonly FilterDefinition<ProductDocument> Published = Filter.Eq(p => p.Status, nameof(ProductStatus.Published));

    public Task<Page<Product>> ListPublishedAsync(PublicProductFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        return MongoOperations.RunAsync(async () =>
        {
            var query = Published;
            if (filter.TenantId is { } tenantId)
            {
                query &= Filter.Eq(p => p.TenantId, tenantId);
            }

            if (filter.CategoryId is { } categoryId)
            {
                query &= Filter.AnyEq(p => p.CategoryTreeIds, categoryId);
            }

            if (filter.BrandId is { } brandId)
            {
                query &= Filter.Eq(p => p.BrandId, brandId);
            }

            // Each pair matches within one attribute, never a key of one attribute with the value of another.
            foreach (var attribute in filter.Attributes)
            {
                query &= Filter.ElemMatch(
                    p => p.Attributes,
                    Builders<AttributeDocument>.Filter.Eq(a => a.Key, attribute.Key) & Builders<AttributeDocument>.Filter.Eq(a => a.ValueKey, attribute.ValueKey));
            }

            if (filter.Text is { } text)
            {
                query &= Filter.Text(text);
            }

            var sort = filter.Sort == CatalogSort.Oldest
                ? Builders<ProductDocument>.Sort.Ascending(p => p.PublishedAt).Ascending(p => p.Id)
                : Builders<ProductDocument>.Sort.Descending(p => p.PublishedAt).Descending(p => p.Id);

            var total = await context.Products.CountDocumentsAsync(query, MongoOperations.CountOptions(context), cancellationToken);
            var documents = await context.Products.Find(query, MongoOperations.FindOptions(context))
                .Sort(sort)
                .Skip(page.Skip)
                .Limit(page.PageSize)
                .ToListAsync(cancellationToken);
            return new Page<Product>([.. documents.Select(CatalogDocumentMapping.ToDomain)], page.Page, page.PageSize, total);
        });
    }

    public Task<Product?> GetPublishedAsync(Guid productId, CancellationToken cancellationToken) =>
        FirstAsync(Published & Filter.Eq(p => p.Id, productId), cancellationToken);

    public Task<Product?> GetPublishedBySlugAsync(Guid tenantId, string slug, CancellationToken cancellationToken) =>
        FirstAsync(Published & Filter.Eq(p => p.TenantId, tenantId) & Filter.Eq(p => p.Slug, slug), cancellationToken);

    private Task<Product?> FirstAsync(FilterDefinition<ProductDocument> query, CancellationToken cancellationToken) =>
        MongoOperations.RunAsync(async () =>
        {
            var document = await context.Products.Find(query, MongoOperations.FindOptions(context)).FirstOrDefaultAsync(cancellationToken);
            return document is null ? null : CatalogDocumentMapping.ToDomain(document);
        });
}
