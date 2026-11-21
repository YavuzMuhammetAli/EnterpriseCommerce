using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.TenantMerchant.Domain.Tenants;

/// <summary>The merchant's public display name: 1 to 200 characters after trimming, without control characters.</summary>
public sealed record MerchantDisplayName
{
    public const int MaxLength = 200;

    private MerchantDisplayName(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out MerchantDisplayName? displayName)
    {
        var trimmed = value?.Trim();
        displayName = trimmed is { Length: > 0 and <= MaxLength } && !trimmed.Any(char.IsControl)
            ? new MerchantDisplayName(trimmed)
            : null;
        return displayName is not null;
    }

    public override string ToString() => Value;
}
