using EnterpriseCommerce.Catalog.Domain.Brands;
using EnterpriseCommerce.Catalog.Domain.Categories;
using EnterpriseCommerce.Catalog.Domain.Common;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// Explicit mapping between the domain and the persistence documents. A stored value that the current rules no longer
/// accept (for example a slug written by a broken process) fails loudly instead of being repaired silently.
/// </summary>
internal static class CatalogDocumentMapping
{
    public static ProductDocument ToDocument(Product product, long version)
    {
        var content = product.Content;
        return new ProductDocument
        {
            Id = product.Id,
            TenantId = product.TenantId,
            Slug = content.Slug.Value,
            Name = content.Name,
            ShortDescription = content.ShortDescription,
            Description = content.Description,
            BrandId = content.BrandId,
            CategoryIds = [.. content.CategoryIds],
            CategoryTreeIds = [.. product.CategoryTreeIds],
            Attributes = [.. content.Attributes.Select(ToDocument)],
            Variants = [.. content.Variants.Select(variant => new VariantDocument
            {
                Id = variant.Id,
                Sku = variant.Sku.Value,
                SkuKey = variant.Sku.Key,
                Options = [.. variant.Options.Select(ToDocument)],
                Amount = variant.Price.Amount,
                Currency = variant.Price.Currency,
                IsActive = variant.IsActive,
            })],
            Media = [.. product.Media.Select(media => new MediaDocument
            {
                Id = media.Id,
                Url = media.Url,
                AltText = media.AltText,
                ContentType = media.ContentType,
            })],
            Status = product.Status.ToString(),
            CreatedAt = product.CreatedAt.UtcDateTime,
            UpdatedAt = product.UpdatedAt.UtcDateTime,
            PublishedAt = product.PublishedAt?.UtcDateTime,
            ArchivedAt = product.ArchivedAt?.UtcDateTime,
            CreatedBy = product.CreatedBy,
            UpdatedBy = product.UpdatedBy,
            Version = version,
            Creation = new CreationDocument
            {
                SubjectId = product.Creation.SubjectId,
                IdempotencyKey = product.Creation.IdempotencyKey,
                RequestHash = product.Creation.RequestHash,
            },
        };
    }

    public static Product ToDomain(ProductDocument document)
    {
        var content = ProductContent.Rehydrate(
            StoredSlug(document.Slug),
            document.Name,
            document.ShortDescription,
            document.Description,
            document.BrandId,
            [.. document.CategoryIds],
            [.. document.Attributes.Select(ToDomain)],
            [.. document.Variants.Select(variant => new ProductVariant(
                variant.Id,
                Sku.TryCreate(variant.Sku, out var sku) ? sku : throw Corrupt("SKU"),
                [.. variant.Options.Select(ToDomain)],
                Money.Rehydrate(variant.Amount, variant.Currency),
                variant.IsActive))]);

        return Product.Rehydrate(
            document.Id,
            document.TenantId,
            content,
            [.. document.CategoryTreeIds],
            [.. document.Media.Select(media => MediaReference.Rehydrate(media.Id, media.Url, media.AltText, media.ContentType))],
            Enum.TryParse<ProductStatus>(document.Status, ignoreCase: false, out var status) ? status : throw Corrupt("product status"),
            Utc(document.CreatedAt),
            Utc(document.UpdatedAt),
            document.PublishedAt is { } publishedAt ? Utc(publishedAt) : null,
            document.ArchivedAt is { } archivedAt ? Utc(archivedAt) : null,
            document.CreatedBy,
            document.UpdatedBy,
            document.Version,
            new ProductCreation(document.Creation.SubjectId, document.Creation.IdempotencyKey, document.Creation.RequestHash));
    }

    public static CategoryDocument ToDocument(Category category, long version) => new()
    {
        Id = category.Id,
        Slug = category.Slug.Value,
        Name = category.Name,
        Description = category.Description,
        ParentId = category.ParentId,
        AncestorIds = [.. category.AncestorIds],
        Status = category.Status.ToString(),
        CreatedAt = category.CreatedAt.UtcDateTime,
        UpdatedAt = category.UpdatedAt.UtcDateTime,
        Version = version,
    };

    public static Category ToDomain(CategoryDocument document) =>
        Category.Rehydrate(
            document.Id,
            StoredSlug(document.Slug),
            document.Name,
            document.Description,
            document.ParentId,
            [.. document.AncestorIds],
            StoredStatus(document.Status),
            Utc(document.CreatedAt),
            Utc(document.UpdatedAt),
            document.Version);

    public static BrandDocument ToDocument(Brand brand, long version) => new()
    {
        Id = brand.Id,
        TenantId = brand.TenantId,
        Slug = brand.Slug.Value,
        Name = brand.Name,
        Description = brand.Description,
        Status = brand.Status.ToString(),
        CreatedAt = brand.CreatedAt.UtcDateTime,
        UpdatedAt = brand.UpdatedAt.UtcDateTime,
        Version = version,
    };

    public static Brand ToDomain(BrandDocument document) =>
        Brand.Rehydrate(
            document.Id,
            document.TenantId,
            StoredSlug(document.Slug),
            document.Name,
            document.Description,
            StoredStatus(document.Status),
            Utc(document.CreatedAt),
            Utc(document.UpdatedAt),
            document.Version);

    private static AttributeDocument ToDocument(CatalogAttribute attribute) =>
        new() { Key = attribute.Key, Value = attribute.Value, ValueKey = attribute.ValueKey };

    private static CatalogAttribute ToDomain(AttributeDocument document) => CatalogAttribute.Rehydrate(document.Key, document.Value);

    private static Slug StoredSlug(string value) => Slug.TryCreate(value, out var slug) ? slug : throw Corrupt("slug");

    private static CatalogStatus StoredStatus(string value) =>
        Enum.TryParse<CatalogStatus>(value, ignoreCase: false, out var status) ? status : throw Corrupt("status");

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static InvalidOperationException Corrupt(string what) => new($"A stored catalog document has an invalid {what}.");
}
