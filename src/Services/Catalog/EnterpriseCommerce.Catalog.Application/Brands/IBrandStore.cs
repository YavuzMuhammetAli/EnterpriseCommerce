using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Brands;

namespace EnterpriseCommerce.Catalog.Application.Brands;

/// <summary>Persistence of tenant brands. Tenant-scoped reads and writes name the tenant.</summary>
public interface IBrandStore
{
    Task<Brand?> GetAsync(Guid tenantId, Guid brandId, CancellationToken cancellationToken);

    /// <summary>
    /// The brands with these IDs, of any tenant and status: public discovery resolves the brands of the products it
    /// returns and shows only active ones.
    /// </summary>
    Task<IReadOnlyList<Brand>> GetManyAsync(IReadOnlyCollection<Guid> brandIds, CancellationToken cancellationToken);

    Task<WriteOutcome> InsertAsync(Brand brand, CancellationToken cancellationToken);

    /// <summary>Replaces the stored brand if it still has <paramref name="expectedVersion"/>.</summary>
    Task<WriteOutcome> ReplaceAsync(Brand brand, long expectedVersion, CancellationToken cancellationToken);

    /// <summary>The tenant's brands, oldest first.</summary>
    Task<Page<Brand>> ListAsync(Guid tenantId, PageRequest page, CancellationToken cancellationToken);
}
