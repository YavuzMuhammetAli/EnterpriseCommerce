namespace EnterpriseCommerce.Catalog.Domain.Products;

/// <summary>
/// A sellable unit of a product (ADR-024 section 3): a stable ID, a SKU unique per tenant, its option values, its list
/// price and whether it is sellable. It carries no stock or availability data; Inventory owns those.
/// </summary>
public sealed record ProductVariant(Guid Id, Sku Sku, IReadOnlyList<CatalogAttribute> Options, Money Price, bool IsActive)
{
    public const int MaxOptions = 10;

    /// <summary>A comparable form of the option set: the sorted <c>key=valueKey</c> pairs.</summary>
    public string OptionSignature =>
        string.Join('\u001f', Options.Select(option => $"{option.Key}={option.ValueKey}").Order(StringComparer.Ordinal));
}

/// <summary>A key and a value as received, before validation.</summary>
public sealed record KeyValueInput(string? Key, string? Value);

/// <summary>A variant as received, before validation. A missing ID creates a new variant; a known ID keeps it.</summary>
public sealed record VariantInput(Guid? Id, string? Sku, IReadOnlyList<KeyValueInput>? Options, decimal? Amount, string? Currency, bool? IsActive);

/// <summary>The editable content of a product as received, before validation. It never contains the tenant.</summary>
public sealed record ProductContentInput(
    string? Slug,
    string? Name,
    string? ShortDescription,
    string? Description,
    Guid? BrandId,
    IReadOnlyList<Guid>? CategoryIds,
    IReadOnlyList<KeyValueInput>? Attributes,
    IReadOnlyList<VariantInput>? Variants);

/// <summary>A media reference as received, before validation. A missing ID creates a new reference.</summary>
public sealed record MediaInput(Guid? Id, string? Url, string? AltText, string? ContentType);
