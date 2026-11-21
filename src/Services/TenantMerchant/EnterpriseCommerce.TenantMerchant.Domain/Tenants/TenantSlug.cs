using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.TenantMerchant.Domain.Tenants;

/// <summary>
/// The tenant's stable, human-readable handle: 3 to 63 characters of lowercase ASCII letters, digits and single
/// hyphens, starting and ending with a letter or digit. It is unique across the platform and never changes.
/// </summary>
/// <remarks>
/// Input is never normalized: an uppercase or padded value is rejected rather than silently changed, so that the
/// caller always knows the exact stored value.
/// </remarks>
public sealed record TenantSlug
{
    public const int MinLength = 3;
    public const int MaxLength = 63;

    private TenantSlug(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out TenantSlug? slug)
    {
        slug = IsValid(value) ? new TenantSlug(value!) : null;
        return slug is not null;
    }

    public override string ToString() => Value;

    private static bool IsValid(string? value)
    {
        if (value is null || value.Length is < MinLength or > MaxLength)
        {
            return false;
        }

        if (!IsLetterOrDigit(value[0]) || !IsLetterOrDigit(value[^1]))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '-')
            {
                if (value[index - 1] == '-')
                {
                    return false;
                }
            }
            else if (!IsLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLetterOrDigit(char character) => character is (>= 'a' and <= 'z') or (>= '0' and <= '9');
}
