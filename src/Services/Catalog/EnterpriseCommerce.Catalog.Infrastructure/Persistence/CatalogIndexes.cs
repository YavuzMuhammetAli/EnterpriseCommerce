using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// Every index of the Catalog database, with an explicit name (PHASE_4_CATALOG.md section 5 lists the query each one
/// serves). Creating an existing identical index is a no-op, so creation is repeatable. Unique indexes back every
/// uniqueness rule; the application never relies on a prior check alone.
/// </summary>
internal static class CatalogIndexes
{
    public static IReadOnlyList<CreateIndexModel<ProductDocument>> Products()
    {
        var keys = Builders<ProductDocument>.IndexKeys;
        return
        [
            new(keys.Ascending(p => p.TenantId).Ascending(p => p.Slug), new CreateIndexOptions { Name = "ux_products_tenant_slug", Unique = true }),
            new(
                keys.Ascending(p => p.TenantId).Ascending("variants.skuKey"),
                new CreateIndexOptions<ProductDocument>
                {
                    Name = "ux_products_tenant_sku",
                    Unique = true,

                    // Drafts without variants have no SKU key and must not collide with each other on a missing value.
                    PartialFilterExpression = Builders<ProductDocument>.Filter.Exists("variants.skuKey"),
                }),
            new(
                keys.Ascending(p => p.TenantId).Ascending("creation.subjectId").Ascending("creation.idempotencyKey"),
                new CreateIndexOptions { Name = "ux_products_tenant_creation", Unique = true }),
            new(keys.Ascending(p => p.TenantId).Descending(p => p.UpdatedAt).Descending(p => p.Id), new CreateIndexOptions { Name = "ix_products_tenant_updated" }),
            new(keys.Ascending(p => p.Status).Descending(p => p.PublishedAt).Descending(p => p.Id), new CreateIndexOptions { Name = "ix_products_public_published" }),
            new(
                keys.Ascending(p => p.Status).Ascending(p => p.CategoryTreeIds).Descending(p => p.PublishedAt).Descending(p => p.Id),
                new CreateIndexOptions { Name = "ix_products_public_category" }),
            new(
                keys.Ascending(p => p.Status).Ascending(p => p.BrandId).Descending(p => p.PublishedAt).Descending(p => p.Id),
                new CreateIndexOptions { Name = "ix_products_public_brand" }),
            new(
                keys.Ascending(p => p.Status).Ascending(p => p.TenantId).Descending(p => p.PublishedAt).Descending(p => p.Id),
                new CreateIndexOptions { Name = "ix_products_public_tenant" }),
            new(
                keys.Ascending("attributes.key").Ascending("attributes.valueKey").Ascending(p => p.Status),
                new CreateIndexOptions { Name = "ix_products_attributes" }),
            new(
                keys.Text(p => p.Name).Text(p => p.ShortDescription).Text(p => p.Description),
                new CreateIndexOptions<ProductDocument>
                {
                    Name = "tx_products_search",
                    DefaultLanguage = "none",
                    Weights = new MongoDB.Bson.BsonDocument { { "name", 10 }, { "shortDescription", 5 }, { "description", 1 } },
                }),
        ];
    }

    public static IReadOnlyList<CreateIndexModel<CategoryDocument>> Categories()
    {
        var keys = Builders<CategoryDocument>.IndexKeys;
        return
        [
            new(keys.Ascending(c => c.Slug), new CreateIndexOptions { Name = "ux_categories_slug", Unique = true }),
            new(keys.Ascending(c => c.ParentId).Ascending(c => c.Status), new CreateIndexOptions { Name = "ix_categories_parent" }),
            new(keys.Ascending(c => c.Status).Ascending(c => c.CreatedAt).Ascending(c => c.Id), new CreateIndexOptions { Name = "ix_categories_listing" }),
        ];
    }

    public static IReadOnlyList<CreateIndexModel<BrandDocument>> Brands()
    {
        var keys = Builders<BrandDocument>.IndexKeys;
        return
        [
            new(keys.Ascending(b => b.TenantId).Ascending(b => b.Slug), new CreateIndexOptions { Name = "ux_brands_tenant_slug", Unique = true }),
            new(keys.Ascending(b => b.TenantId).Ascending(b => b.CreatedAt).Ascending(b => b.Id), new CreateIndexOptions { Name = "ix_brands_tenant_created" }),
        ];
    }

    /// <summary>Every index name, per collection: what readiness and the tests expect to exist.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Names() => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [CatalogMongoContext.ProductsCollection] = [.. Products().Select(index => index.Options.Name)],
        [CatalogMongoContext.CategoriesCollection] = [.. Categories().Select(index => index.Options.Name)],
        [CatalogMongoContext.BrandsCollection] = [.. Brands().Select(index => index.Options.Name)],
    };

    public static async Task EnsureAsync(CatalogMongoContext context, CancellationToken cancellationToken)
    {
        await context.Products.Indexes.CreateManyAsync(Products(), cancellationToken);
        await context.Categories.Indexes.CreateManyAsync(Categories(), cancellationToken);
        await context.Brands.Indexes.CreateManyAsync(Brands(), cancellationToken);
    }
}
