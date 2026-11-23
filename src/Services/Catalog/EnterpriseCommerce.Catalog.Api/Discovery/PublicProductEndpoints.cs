using EnterpriseCommerce.Catalog.Api.Common;
using EnterpriseCommerce.Catalog.Api.Products;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Application.Discovery;
using EnterpriseCommerce.Catalog.Domain.Common;
using EnterpriseCommerce.Catalog.Domain.Products;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Catalog.Api.Discovery;

internal sealed record PublicBrandResponse(Guid Id, string Slug, string Name);

internal sealed record PublicCategoryReference(Guid Id, string Slug, string Name);

/// <summary>A published product as the public sees it. The tenant ID identifies the selling merchant.</summary>
internal sealed record PublicProductResponse(
    Guid Id,
    Guid TenantId,
    string Slug,
    string Name,
    string? ShortDescription,
    string? Description,
    PublicBrandResponse? Brand,
    IReadOnlyList<PublicCategoryReference> Categories,
    IReadOnlyList<KeyValueResponse> Attributes,
    IReadOnlyList<VariantResponse> Variants,
    IReadOnlyList<MediaResponse> Media,
    DateTimeOffset? PublishedAt)
{
    public static PublicProductResponse Of(PublicProductView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var product = view.Product;
        var content = product.Content;
        return new PublicProductResponse(
            product.Id,
            product.TenantId,
            content.Slug.Value,
            content.Name,
            content.ShortDescription,
            content.Description,
            view.Brand is { } brand ? new PublicBrandResponse(brand.Id, brand.Slug.Value, brand.Name) : null,
            [.. view.Categories.Select(category => new PublicCategoryReference(category.Id, category.Slug.Value, category.Name))],
            [.. content.Attributes.Select(attribute => new KeyValueResponse(attribute.Key, attribute.Value))],
            [.. view.Variants.Select(MerchantProductResponse.Variant)],
            [.. product.Media.Select(media => new MediaResponse(media.Id, media.Url, media.AltText, media.ContentType))],
            product.PublishedAt);
    }
}

/// <summary>
/// Public discovery (CATALOG_API_CONTRACT.md section 2): anonymous, published products only. Every filter and sort is
/// a validated value from a closed set; no client expression reaches the store (ADR-024 section 9).
/// </summary>
internal static class PublicProductEndpoints
{
    public const int MaxAttributeFilters = 5;

    public static IEndpointRouteBuilder MapPublicProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var products = endpoints.MapGroup("/api/v1/catalog/products").AllowAnonymous();
        products.MapGet("/", ListAsync);
        products.MapGet("/lookup", LookupAsync);
        products.MapGet("/{productId:guid}", GetAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        [FromQuery] string? q,
        [FromQuery] string? categoryId,
        [FromQuery] string? brandId,
        [FromQuery] string? tenantId,
        [FromQuery(Name = "attr")] string[]? attributes,
        [FromQuery] string? sort,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        PublicCatalogHandler handler,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!PageRequest.TryCreate(page, pageSize, PageRequest.MaxPublicPage, out var pageRequest, out var pageErrors))
        {
            foreach (var (field, messages) in pageErrors)
            {
                errors[field] = messages;
            }
        }

        if (!SearchText.TryCreate(q, out var text))
        {
            QueryParameters.Add(errors, "q", $"q must be {SearchText.Rule}");
        }

        if (!QueryParameters.TryParseSort(sort, out var sortOrder))
        {
            QueryParameters.Add(errors, "sort", "sort must be newest or oldest.");
        }

        var category = OptionalId(categoryId, "categoryId", errors);
        var brand = OptionalId(brandId, "brandId", errors);
        var tenant = OptionalId(tenantId, "tenantId", errors);
        var attributeFilters = ParseAttributes(attributes, errors);
        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var result = await handler.ListAsync(new PublicProductFilter(text, category, brand, tenant, attributeFilters, sortOrder), pageRequest!, cancellationToken);
        return Results.Ok(new PageResponse<PublicProductResponse>([.. result.Items.Select(PublicProductResponse.Of)], result.PageNumber, result.PageSize, result.TotalCount));
    }

    private static async Task<IResult> GetAsync(Guid productId, PublicCatalogHandler handler, CancellationToken cancellationToken) =>
        await handler.GetAsync(productId, cancellationToken) is { } view
            ? Results.Ok(PublicProductResponse.Of(view))
            : Problems.For(CommandStatus.NotFound, "product");

    private static async Task<IResult> LookupAsync(
        [FromQuery] string? tenantId,
        [FromQuery] string? slug,
        PublicCatalogHandler handler,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var tenant = OptionalId(tenantId, "tenantId", errors);
        if (tenant is null && !errors.ContainsKey("tenantId"))
        {
            QueryParameters.Add(errors, "tenantId", "tenantId is required.");
        }

        if (!Slug.TryCreate(slug, out var validSlug))
        {
            QueryParameters.Add(errors, "slug", $"slug must be {Slug.Rule}");
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        return await handler.LookupAsync(tenant!.Value, validSlug!.Value, cancellationToken) is { } view
            ? Results.Ok(PublicProductResponse.Of(view))
            : Problems.For(CommandStatus.NotFound, "product");
    }

    private static Guid? OptionalId(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty)
        {
            return id;
        }

        QueryParameters.Add(errors, field, $"{field} must be a GUID.");
        return null;
    }

    /// <summary>Parses <c>attr=key:value</c> pairs: at most five, each a valid attribute key and value.</summary>
    private static List<AttributeFilter> ParseAttributes(string[]? values, Dictionary<string, string[]> errors)
    {
        var filters = new List<AttributeFilter>();
        if (values is not { Length: > 0 })
        {
            return filters;
        }

        if (values.Length > MaxAttributeFilters)
        {
            QueryParameters.Add(errors, "attr", $"At most {MaxAttributeFilters} attribute filters are allowed.");
            return filters;
        }

        foreach (var value in values)
        {
            var separator = value?.IndexOf(':', StringComparison.Ordinal) ?? -1;
            if (separator <= 0 || !CatalogAttribute.TryCreate(value![..separator], value[(separator + 1)..], out var attribute, out _, out _))
            {
                QueryParameters.Add(errors, "attr", $"attr must be key:value, with a key of {CatalogAttribute.KeyRule} and a value of 1 to {CatalogAttribute.MaxValueLength} characters.");
                return filters;
            }

            filters.Add(new AttributeFilter(attribute.Key, attribute.ValueKey));
        }

        return filters;
    }
}
