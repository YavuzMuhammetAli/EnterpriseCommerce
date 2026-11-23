using EnterpriseCommerce.Catalog.Application.Categories;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Categories;
using EnterpriseCommerce.Catalog.Domain.Common;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>The platform category taxonomy in the <c>categories</c> collection.</summary>
internal sealed class CategoryStore(CatalogMongoContext context, CatalogIndexState indexes) : ICategoryStore
{
    private static readonly FilterDefinitionBuilder<CategoryDocument> Filter = Builders<CategoryDocument>.Filter;

    public Task<Category?> GetAsync(Guid categoryId, CancellationToken cancellationToken) =>
        MongoOperations.RunAsync(async () =>
        {
            var document = await context.Categories.Find(Filter.Eq(c => c.Id, categoryId), MongoOperations.FindOptions(context)).FirstOrDefaultAsync(cancellationToken);
            return document is null ? null : CatalogDocumentMapping.ToDomain(document);
        });

    public Task<IReadOnlyList<Category>> GetManyAsync(IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        if (categoryIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Category>>([]);
        }

        return MongoOperations.RunAsync<IReadOnlyList<Category>>(async () =>
        {
            var documents = await context.Categories.Find(Filter.In(c => c.Id, categoryIds), MongoOperations.FindOptions(context)).ToListAsync(cancellationToken);
            return [.. documents.Select(CatalogDocumentMapping.ToDomain)];
        });
    }

    public Task<bool> HasActiveChildrenAsync(Guid categoryId, CancellationToken cancellationToken) =>
        MongoOperations.RunAsync(async () =>
            await context.Categories
                .Find(Filter.Eq(c => c.ParentId, categoryId) & Filter.Eq(c => c.Status, nameof(CatalogStatus.Active)), MongoOperations.FindOptions(context))
                .Limit(1)
                .AnyAsync(cancellationToken));

    public Task<WriteOutcome> InsertAsync(Category category, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        indexes.EnsureWritable();
        return MongoOperations.RunAsync(async () =>
        {
            try
            {
                await context.Categories.InsertOneAsync(CatalogDocumentMapping.ToDocument(category, category.Version), cancellationToken: cancellationToken);
                return WriteOutcome.Written;
            }
            catch (MongoWriteException exception) when (MongoOperations.DuplicateKeyIndex(exception) is { } index)
            {
                return MongoOperations.SlugDuplicate(index);
            }
        });
    }

    public Task<WriteOutcome> ReplaceAsync(Category category, long expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(category);
        indexes.EnsureWritable();
        return MongoOperations.RunAsync(async () =>
        {
            try
            {
                var result = await context.Categories.ReplaceOneAsync(
                    Filter.Eq(c => c.Id, category.Id) & Filter.Eq(c => c.Version, expectedVersion),
                    CatalogDocumentMapping.ToDocument(category, expectedVersion + 1),
                    cancellationToken: cancellationToken);
                if (result.MatchedCount == 0)
                {
                    return WriteOutcome.VersionConflict;
                }

                category.AcceptStoredVersion(expectedVersion + 1);
                return WriteOutcome.Written;
            }
            catch (MongoWriteException exception) when (MongoOperations.DuplicateKeyIndex(exception) is { } index)
            {
                return MongoOperations.SlugDuplicate(index);
            }
        });
    }

    public Task<Page<Category>> ListAsync(bool includeRetired, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        return MongoOperations.RunAsync(async () =>
        {
            var query = includeRetired ? Filter.Empty : Filter.Eq(c => c.Status, nameof(CatalogStatus.Active));
            var total = await context.Categories.CountDocumentsAsync(query, MongoOperations.CountOptions(context), cancellationToken);
            var documents = await context.Categories.Find(query, MongoOperations.FindOptions(context))
                .Sort(Builders<CategoryDocument>.Sort.Ascending(c => c.CreatedAt).Ascending(c => c.Id))
                .Skip(page.Skip)
                .Limit(page.PageSize)
                .ToListAsync(cancellationToken);
            return new Page<Category>([.. documents.Select(CatalogDocumentMapping.ToDomain)], page.Page, page.PageSize, total);
        });
    }
}
