using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Application.Products;
using EnterpriseCommerce.Catalog.Domain.Products;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// The product aggregate in the <c>products</c> collection: one document per product, written atomically. Every
/// filter includes the tenant, so another tenant's product is never matched, whatever its ID.
/// </summary>
internal sealed class ProductStore(CatalogMongoContext context, CatalogIndexState indexes) : IProductStore
{
    private static readonly FilterDefinitionBuilder<ProductDocument> Filter = Builders<ProductDocument>.Filter;

    public Task<Product?> GetAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken) =>
        MongoOperations.RunAsync(async () =>
        {
            var document = await context.Products
                .Find(Filter.Eq(p => p.Id, productId) & Filter.Eq(p => p.TenantId, tenantId), MongoOperations.FindOptions(context))
                .FirstOrDefaultAsync(cancellationToken);
            return document is null ? null : CatalogDocumentMapping.ToDomain(document);
        });

    public Task<Product?> FindByCreationKeyAsync(Guid tenantId, string subjectId, string idempotencyKey, CancellationToken cancellationToken) =>
        MongoOperations.RunAsync(async () =>
        {
            var filter = Filter.Eq(p => p.TenantId, tenantId)
                & Filter.Eq(p => p.Creation.SubjectId, subjectId)
                & Filter.Eq(p => p.Creation.IdempotencyKey, idempotencyKey);
            var document = await context.Products.Find(filter, MongoOperations.FindOptions(context)).FirstOrDefaultAsync(cancellationToken);
            return document is null ? null : CatalogDocumentMapping.ToDomain(document);
        });

    public Task<WriteOutcome> InsertAsync(Product product, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        indexes.EnsureWritable();
        return MongoOperations.RunAsync(async () =>
        {
            try
            {
                await context.Products.InsertOneAsync(CatalogDocumentMapping.ToDocument(product, product.Version), cancellationToken: cancellationToken);
                return WriteOutcome.Written;
            }
            catch (MongoWriteException exception) when (MongoOperations.DuplicateKeyIndex(exception) is { } index)
            {
                return MongoOperations.ProductDuplicate(index);
            }
        });
    }

    public Task<WriteOutcome> ReplaceAsync(Product product, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(product);
        indexes.EnsureWritable();
        return MongoOperations.RunAsync(async () =>
        {
            var filter = Filter.Eq(p => p.Id, product.Id) & Filter.Eq(p => p.TenantId, product.TenantId) & Filter.Eq(p => p.Version, expectedVersion);
            try
            {
                var result = await context.Products.ReplaceOneAsync(
                    filter, CatalogDocumentMapping.ToDocument(product, expectedVersion + 1), cancellationToken: cancellationToken);
                if (result.MatchedCount == 0)
                {
                    return WriteOutcome.VersionConflict;
                }

                product.AcceptStoredVersion(expectedVersion + 1);
                return WriteOutcome.Written;
            }
            catch (MongoWriteException exception) when (MongoOperations.DuplicateKeyIndex(exception) is { } index)
            {
                return MongoOperations.ProductDuplicate(index);
            }
        });
    }

    public Task<Page<Product>> ListAsync(Guid tenantId, MerchantProductFilter filter, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);
        return MongoOperations.RunAsync(async () =>
        {
            var query = Filter.Eq(p => p.TenantId, tenantId);
            if (filter.Status is { } status)
            {
                query &= Filter.Eq(p => p.Status, status.ToString());
            }

            if (filter.SkuKey is { } skuKey)
            {
                query &= Filter.Eq("variants.skuKey", skuKey);
            }

            if (filter.Text is { } text)
            {
                query &= Filter.Text(text);
            }

            var sort = filter.Sort == CatalogSort.Oldest
                ? Builders<ProductDocument>.Sort.Ascending(p => p.UpdatedAt).Ascending(p => p.Id)
                : Builders<ProductDocument>.Sort.Descending(p => p.UpdatedAt).Descending(p => p.Id);

            var total = await context.Products.CountDocumentsAsync(query, MongoOperations.CountOptions(context), cancellationToken);
            var documents = await context.Products.Find(query, MongoOperations.FindOptions(context))
                .Sort(sort)
                .Skip(page.Skip)
                .Limit(page.PageSize)
                .ToListAsync(cancellationToken);
            return new Page<Product>([.. documents.Select(CatalogDocumentMapping.ToDomain)], page.Page, page.PageSize, total);
        });
    }
}
