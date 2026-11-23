using EnterpriseCommerce.Catalog.Application.Brands;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Brands;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>Tenant brands in the <c>brands</c> collection. Tenant-scoped filters always include the tenant.</summary>
internal sealed class BrandStore(CatalogMongoContext context, CatalogIndexState indexes) : IBrandStore
{
    private static readonly FilterDefinitionBuilder<BrandDocument> Filter = Builders<BrandDocument>.Filter;

    public Task<Brand?> GetAsync(Guid tenantId, Guid brandId, CancellationToken cancellationToken) =>
        MongoOperations.RunAsync(async () =>
        {
            var document = await context.Brands
                .Find(Filter.Eq(b => b.Id, brandId) & Filter.Eq(b => b.TenantId, tenantId), MongoOperations.FindOptions(context))
                .FirstOrDefaultAsync(cancellationToken);
            return document is null ? null : CatalogDocumentMapping.ToDomain(document);
        });

    public Task<IReadOnlyList<Brand>> GetManyAsync(IReadOnlyCollection<Guid> brandIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brandIds);
        if (brandIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Brand>>([]);
        }

        return MongoOperations.RunAsync<IReadOnlyList<Brand>>(async () =>
        {
            var documents = await context.Brands.Find(Filter.In(b => b.Id, brandIds), MongoOperations.FindOptions(context)).ToListAsync(cancellationToken);
            return [.. documents.Select(CatalogDocumentMapping.ToDomain)];
        });
    }

    public Task<WriteOutcome> InsertAsync(Brand brand, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brand);
        indexes.EnsureWritable();
        return MongoOperations.RunAsync(async () =>
        {
            try
            {
                await context.Brands.InsertOneAsync(CatalogDocumentMapping.ToDocument(brand, brand.Version), cancellationToken: cancellationToken);
                return WriteOutcome.Written;
            }
            catch (MongoWriteException exception) when (MongoOperations.DuplicateKeyIndex(exception) is { } index)
            {
                return MongoOperations.SlugDuplicate(index);
            }
        });
    }

    public Task<WriteOutcome> ReplaceAsync(Brand brand, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(brand);
        indexes.EnsureWritable();
        return MongoOperations.RunAsync(async () =>
        {
            try
            {
                var result = await context.Brands.ReplaceOneAsync(
                    Filter.Eq(b => b.Id, brand.Id) & Filter.Eq(b => b.TenantId, brand.TenantId) & Filter.Eq(b => b.Version, expectedVersion),
                    CatalogDocumentMapping.ToDocument(brand, expectedVersion + 1),
                    cancellationToken: cancellationToken);
                if (result.MatchedCount == 0)
                {
                    return WriteOutcome.VersionConflict;
                }

                brand.AcceptStoredVersion(expectedVersion + 1);
                return WriteOutcome.Written;
            }
            catch (MongoWriteException exception) when (MongoOperations.DuplicateKeyIndex(exception) is { } index)
            {
                return MongoOperations.SlugDuplicate(index);
            }
        });
    }

    public Task<Page<Brand>> ListAsync(Guid tenantId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        return MongoOperations.RunAsync(async () =>
        {
            var query = Filter.Eq(b => b.TenantId, tenantId);
            var total = await context.Brands.CountDocumentsAsync(query, MongoOperations.CountOptions(context), cancellationToken);
            var documents = await context.Brands.Find(query, MongoOperations.FindOptions(context))
                .Sort(Builders<BrandDocument>.Sort.Ascending(b => b.CreatedAt).Ascending(b => b.Id))
                .Skip(page.Skip)
                .Limit(page.PageSize)
                .ToListAsync(cancellationToken);
            return new Page<Brand>([.. documents.Select(CatalogDocumentMapping.ToDomain)], page.Page, page.PageSize, total);
        });
    }
}
