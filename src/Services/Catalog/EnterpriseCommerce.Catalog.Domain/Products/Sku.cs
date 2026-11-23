using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.Catalog.Domain.Products;

/// <summary>
/// A stock keeping unit: 1 to 64 characters of letters, digits, <c>.</c>, <c>_</c> and <c>-</c>. SKUs are compared
/// case-insensitively through <see cref="Key"/> and are unique per tenant across all its products (ADR-024 section 3).
/// </summary>
public sealed record Sku
{
    public const int MaxLength = 64;

    public const string Rule = "1 to 64 characters of letters, digits, '.', '_' or '-'.";

    private Sku(string value) => Value = value;

    /// <summary>The SKU as the merchant wrote it.</summary>
    public string Value { get; }

    /// <summary>The comparison key: the SKU in upper case (invariant culture).</summary>
    public string Key => Value.ToUpperInvariant();

    public static bool TryCreate(string? value, [NotNullWhen(true)] out Sku? sku)
    {
        sku = value is { Length: > 0 and <= MaxLength }
            && value.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or '-')
                ? new Sku(value)
                : null;
        return sku is not null;
    }

    public override string ToString() => Value;
}
