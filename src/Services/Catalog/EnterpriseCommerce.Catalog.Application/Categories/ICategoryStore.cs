using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Categories;

namespace EnterpriseCommerce.Catalog.Application.Categories;

/// <summary>Persistence of the platform category taxonomy.</summary>
public interface ICategoryStore
{
    Task<Category?> GetAsync(Guid categoryId, CancellationToken cancellationToken);

    /// <summary>The categories with these IDs that exist, in any status.</summary>
    Task<IReadOnlyList<Category>> GetManyAsync(IReadOnlyCollection<Guid> categoryIds, CancellationToken cancellationToken);

    Task<bool> HasActiveChildrenAsync(Guid categoryId, CancellationToken cancellationToken);

    Task<WriteOutcome> InsertAsync(Category category, CancellationToken cancellationToken);

    /// <summary>Replaces the stored category if it still has <paramref name="expectedVersion"/>.</summary>
    Task<WriteOutcome> ReplaceAsync(Category category, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>Categories oldest first, so that every parent precedes its children.</summary>
    Task<Page<Category>> ListAsync(bool includeRetired, PageRequest page, CancellationToken cancellationToken);
}
