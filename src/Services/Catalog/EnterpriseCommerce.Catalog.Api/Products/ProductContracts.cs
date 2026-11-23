using System.Text.Json.Serialization;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.Api.Products;

// Request bodies reject members the contract does not define (400), at every level: no tenant, status, version, owner
// or stock field can be smuggled into a product (no over-posting).

/// <summary>A key and a value (product attribute or variant option).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record KeyValueRequest(string? Key, string? Value);

/// <summary>A list price.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PriceRequest(decimal? Amount, string? Currency);

/// <summary>A variant. Without <c>id</c> it is a new variant; with the ID of an existing variant it keeps that ID.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VariantRequest(Guid? Id, string? Sku, IReadOnlyList<KeyValueRequest>? Options, PriceRequest? Price, bool? IsActive);

/// <summary>
/// <c>POST</c> and <c>PUT</c> body of a product: its complete editable content. The tenant is the route tenant; the
/// lifecycle has its own commands; media has its own endpoint.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ProductRequest(
    string? Slug,
    string? Name,
    string? ShortDescription,
    string? Description,
    Guid? BrandId,
    IReadOnlyList<Guid>? CategoryIds,
    IReadOnlyList<KeyValueRequest>? Attributes,
    IReadOnlyList<VariantRequest>? Variants)
{
    public ProductContentInput ToInput() => new(
        Slug,
        Name,
        ShortDescription,
        Description,
        BrandId,
        CategoryIds,
        Attributes?.Select(ToInput).ToList(),
        Variants?.Select(variant => variant is null
            ? null!
            : new VariantInput(variant.Id, variant.Sku, variant.Options?.Select(ToInput).ToList(), variant.Price?.Amount, variant.Price?.Currency, variant.IsActive)).ToList());

    private static KeyValueInput ToInput(KeyValueRequest entry) => entry is null ? null! : new KeyValueInput(entry.Key, entry.Value);
}

/// <summary>A media reference. Without <c>id</c> it is new; with the ID of an existing reference it keeps that ID.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MediaRequest(Guid? Id, string? Url, string? AltText, string? ContentType);

/// <summary><c>PUT .../media</c> body: the complete, ordered list of media references.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MediaListRequest(IReadOnlyList<MediaRequest>? Media)
{
    public IReadOnlyList<MediaInput> ToInput() =>
        Media?.Select(media => media is null ? null! : new MediaInput(media.Id, media.Url, media.AltText, media.ContentType)).ToList() ?? [];
}

internal sealed record KeyValueResponse(string Key, string Value);

internal sealed record PriceResponse(decimal Amount, string Currency);

internal sealed record VariantResponse(Guid Id, string Sku, IReadOnlyList<KeyValueResponse> Options, PriceResponse Price, bool IsActive);

internal sealed record MediaResponse(Guid Id, string Url, string? AltText, string ContentType);

/// <summary>A product as its merchant sees it: every status, the version, never the idempotency record.</summary>
internal sealed record MerchantProductResponse(
    Guid Id,
    Guid TenantId,
    string Slug,
    string Name,
    string? ShortDescription,
    string? Description,
    Guid? BrandId,
    IReadOnlyList<Guid> CategoryIds,
    IReadOnlyList<KeyValueResponse> Attributes,
    IReadOnlyList<VariantResponse> Variants,
    IReadOnlyList<MediaResponse> Media,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    long Version)
{
    public static MerchantProductResponse Of(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var content = product.Content;
        return new MerchantProductResponse(
            product.Id,
            product.TenantId,
            content.Slug.Value,
            content.Name,
            content.ShortDescription,
            content.Description,
            content.BrandId,
            content.CategoryIds,
            [.. content.Attributes.Select(attribute => new KeyValueResponse(attribute.Key, attribute.Value))],
            [.. content.Variants.Select(Variant)],
            [.. product.Media.Select(media => new MediaResponse(media.Id, media.Url, media.AltText, media.ContentType))],
            product.Status.ToString(),
            product.CreatedAt,
            product.UpdatedAt,
            product.PublishedAt,
            product.ArchivedAt,
            product.Version);
    }

    public static VariantResponse Variant(ProductVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return new VariantResponse(
            variant.Id,
            variant.Sku.Value,
            [.. variant.Options.Select(option => new KeyValueResponse(option.Key, option.Value))],
            new PriceResponse(variant.Price.Amount, variant.Price.Currency),
            variant.IsActive);
    }
}

/// <summary>A page of a collection (docs/api/README.md, pagination convention).</summary>
internal sealed record PageResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);
