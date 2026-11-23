using System.Diagnostics.CodeAnalysis;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.Domain.Products;

/// <summary>
/// A product attribute or a variant option: a key of 1 to 40 characters (<c>a-z</c>, <c>0-9</c> and <c>-</c>, not
/// starting with <c>-</c>) and a single-line value of 1 to 100 characters. Filters compare values case-insensitively
/// through <see cref="ValueKey"/>.
/// </summary>
public sealed record CatalogAttribute
{
    public const int MaxKeyLength = 40;
    public const int MaxValueLength = 100;

    public const string KeyRule = "1 to 40 characters of a-z, 0-9 or '-', not starting with '-'.";

    private CatalogAttribute(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public string Value { get; }

    /// <summary>The comparison key of the value: lower case (invariant culture).</summary>
    public string ValueKey => Value.ToLowerInvariant();

    public static bool IsValidKey([NotNullWhen(true)] string? key) =>
        key is { Length: > 0 and <= MaxKeyLength }
        && key[0] != '-'
        && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    public static bool TryCreate(string? key, string? value, [NotNullWhen(true)] out CatalogAttribute? attribute, out string? keyError, out string? valueError)
    {
        keyError = IsValidKey(key) ? null : $"The key must be {KeyRule}";
        valueError = CatalogText.TryRequired(value, MaxValueLength, out var text)
            ? null
            : $"The value must be 1 to {MaxValueLength} characters on one line.";

        attribute = keyError is null && valueError is null ? new CatalogAttribute(key!, text) : null;
        return attribute is not null;
    }

    /// <summary>Restores a stored attribute, which was valid when it was written.</summary>
    public static CatalogAttribute Rehydrate(string key, string value) => new(key, value);
}
