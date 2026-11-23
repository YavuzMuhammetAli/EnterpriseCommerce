using EnterpriseCommerce.Catalog.Application.Brands;
using EnterpriseCommerce.Catalog.Application.Categories;
using EnterpriseCommerce.Catalog.Domain.Common;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.Application.Products;

/// <summary>The checked references of product content: assignment errors, the category tree and the reference state.</summary>
public sealed record ReferenceCheck(ValidationErrors Errors, IReadOnlyList<Guid> CategoryTreeIds, ReferenceState State);

/// <summary>
/// Application-level reference validation (MongoDB has no foreign keys; ADR-024 section 11).
/// <list type="bullet">
/// <item>A newly assigned category must exist and be active; a newly assigned brand must be an active brand of the
/// product's own tenant. A brand of another tenant is reported exactly like a missing one.</item>
/// <item>A category or brand the product already had may since have been retired: the product keeps it (nothing is
/// orphaned), and the reference state blocks publication until it is replaced.</item>
/// <item>The category tree is the categories plus their stored ancestry, which never changes.</item>
/// </list>
/// </summary>
public sealed class ProductReferences(ICategoryStore categories, IBrandStore brands)
{
    public async Task<ReferenceCheck> CheckAsync(Guid tenantId, ProductContent content, ProductContent? current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var errors = new ValidationErrors();
        var found = (await categories.GetManyAsync(content.CategoryIds, cancellationToken)).ToDictionary(category => category.Id);
        var previous = current?.CategoryIds.ToHashSet() ?? [];
        foreach (var id in content.CategoryIds)
        {
            if (!found.TryGetValue(id, out var category))
            {
                errors.Add("categoryIds", $"The category {id} does not exist.");
            }
            else if (category.Status != CatalogStatus.Active && !previous.Contains(id))
            {
                errors.Add("categoryIds", $"The category {id} is retired and cannot be assigned.");
            }
        }

        var brandActive = true;
        if (content.BrandId is { } brandId)
        {
            var brand = await brands.GetAsync(tenantId, brandId, cancellationToken);
            brandActive = brand is { Status: CatalogStatus.Active };
            if (brand is null)
            {
                errors.Add("brandId", "The brand does not exist.");
            }
            else if (!brandActive && current?.BrandId != brandId)
            {
                errors.Add("brandId", "The brand is retired and cannot be assigned.");
            }
        }

        var tree = content.CategoryIds
            .Where(found.ContainsKey)
            .SelectMany(id => found[id].TreeIds)
            .Distinct()
            .ToList();
        var allActive = content.CategoryIds.All(id => found.TryGetValue(id, out var category) && category.Status == CatalogStatus.Active);
        return new ReferenceCheck(errors, tree, new ReferenceState(allActive, brandActive));
    }
}
