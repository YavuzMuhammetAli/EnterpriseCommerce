using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Brands;
using EnterpriseCommerce.Catalog.Domain.Categories;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

/// <summary>Valid domain objects for persistence tests.</summary>
public static class CatalogSamples
{
    public static DateTimeOffset Now => CatalogClock.Now(TimeProvider.System);

    public static ProductContentInput Input(string slug, params string[] skus) =>
        new(
            slug,
            $"Product {slug}",
            "Short.",
            "Long\ndescription.",
            null,
            [],
            [new KeyValueInput("material", "Cotton")],
            [.. skus.Select((sku, index) => new VariantInput(null, sku, [new KeyValueInput("size", $"S{index}")], 19.99m, "EUR", true))]);

    public static Product Product(Guid tenantId, string slug, params string[] skus) => Product(tenantId, Input(slug, skus));

    public static Product Product(Guid tenantId, ProductContentInput input, string idempotencyKey = "")
    {
        Assert.True(ProductContent.TryCreate(input, [], Guid.CreateVersion7, out var content, out var errors), string.Join("; ", errors.ToDictionary().Keys));
        var key = idempotencyKey.Length > 0 ? idempotencyKey : Guid.NewGuid().ToString("N");
        return EnterpriseCommerce.Catalog.Domain.Products.Product.Create(
            Guid.CreateVersion7(), tenantId, content, content.CategoryIds, new ProductCreation("owner-1", key, "hash"), Now);
    }

    public static Category RootCategory(string slug)
    {
        Assert.True(Category.TryCreateDetails(slug, $"Category {slug}", null, out var details, out _));
        Assert.Equal(CategoryOutcome.Changed, Category.TryCreate(Guid.CreateVersion7(), details, false, null, Now, out var category));
        return category!;
    }

    public static Brand Brand(Guid tenantId, string slug)
    {
        Assert.True(EnterpriseCommerce.Catalog.Domain.Brands.Brand.TryCreateDetails(slug, $"Brand {slug}", null, out var details, out _));
        return EnterpriseCommerce.Catalog.Domain.Brands.Brand.Create(Guid.CreateVersion7(), tenantId, details, Now);
    }
}
