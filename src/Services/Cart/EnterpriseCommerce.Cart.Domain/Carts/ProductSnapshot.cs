namespace EnterpriseCommerce.Cart.Domain.Carts;

/// <summary>One option of a variant, for example size M. The bounds mirror Catalog's attribute limits.</summary>
public sealed record VariantOption(string Key, string Value)
{
    public const int MaxKeyLength = 40;
    public const int MaxValueLength = 100;

    public static bool IsValid(string? key, string? value) =>
        key is { Length: > 0 and <= MaxKeyLength } && !HasControlCharacters(key)
        && value is { Length: > 0 and <= MaxValueLength } && !HasControlCharacters(value);

    internal static bool HasControlCharacters(string value) => value.Any(char.IsControl);
}

/// <summary>The product image a cart line shows: Catalog's first media reference.</summary>
public sealed record ProductImage(string Url, string? AltText)
{
    public const int MaxUrlLength = 2048;
    public const int MaxAltTextLength = 250;

    public static bool IsValid(string? url, string? altText) =>
        url is { Length: > 0 and <= MaxUrlLength }
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (altText is null || (altText.Length <= MaxAltTextLength && !VariantOption.HasControlCharacters(altText)));
}

/// <summary>
/// What a cart line copies from Catalog when a variant is added or refreshed (ADR-029 section 5): only what the cart
/// shows. It is a point-in-time copy, never the source of truth: Catalog owns the product and its price.
/// </summary>
public sealed record ProductSnapshot
{
    public const int MaxNameLength = 200;
    public const int MaxSkuLength = 64;
    public const int MaxOptions = 10;

    private ProductSnapshot(
        Guid productId, Guid variantId, Guid tenantId, string name, string sku, IReadOnlyList<VariantOption> options, ProductImage? image, Money unitPrice)
    {
        ProductId = productId;
        VariantId = variantId;
        TenantId = tenantId;
        Name = name;
        Sku = sku;
        Options = options;
        Image = image;
        UnitPrice = unitPrice;
    }

    public Guid ProductId { get; }

    public Guid VariantId { get; }

    /// <summary>The selling merchant: the product's tenant in Catalog.</summary>
    public Guid TenantId { get; }

    public string Name { get; }

    public string Sku { get; }

    public IReadOnlyList<VariantOption> Options { get; }

    public ProductImage? Image { get; }

    public Money UnitPrice { get; }

    /// <summary>A snapshot, or <c>null</c> when a value is outside the bounds (a Catalog answer Cart must not trust).</summary>
    public static ProductSnapshot? TryCreate(
        Guid productId, Guid variantId, Guid tenantId, string? name, string? sku, IReadOnlyList<VariantOption>? options, ProductImage? image, Money? unitPrice)
    {
        if (productId == Guid.Empty || variantId == Guid.Empty || tenantId == Guid.Empty
            || name is not { Length: > 0 and <= MaxNameLength } || VariantOption.HasControlCharacters(name)
            || sku is not { Length: > 0 and <= MaxSkuLength } || VariantOption.HasControlCharacters(sku)
            || options is null || options.Count > MaxOptions || options.Any(option => !VariantOption.IsValid(option?.Key, option?.Value))
            || (image is not null && !ProductImage.IsValid(image.Url, image.AltText))
            || unitPrice is null)
        {
            return null;
        }

        return new ProductSnapshot(productId, variantId, tenantId, name, sku, [.. options], image, unitPrice);
    }
}
