using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.Catalog.Domain.Common;

/// <summary>
/// A URL handle of a product, category or brand: 3 to 100 characters of <c>a-z</c> and <c>0-9</c> in groups separated
/// by single hyphens. A non-conforming value is refused, never normalized (ADR-024 section 3).
/// </summary>
public sealed record Slug
{
    public const int MinLength = 3;
    public const int MaxLength = 100;

    public const string Rule = "3 to 100 characters of a-z and 0-9, in groups separated by single hyphens.";

    private Slug(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out Slug? slug)
    {
        slug = IsValid(value) ? new Slug(value!) : null;
        return slug is not null;
    }

    public override string ToString() => Value;

    private static bool IsValid(string? value)
    {
        if (value is not { Length: >= MinLength and <= MaxLength } || value[0] == '-' || value[^1] == '-')
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            var allowed = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') || (c == '-' && value[index - 1] != '-');
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
