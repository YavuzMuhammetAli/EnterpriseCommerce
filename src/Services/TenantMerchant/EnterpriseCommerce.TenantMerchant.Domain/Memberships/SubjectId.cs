using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.TenantMerchant.Domain.Memberships;

/// <summary>
/// An Identity subject identifier (the access token's <c>sub</c>): opaque, 1 to 128 characters, without whitespace or
/// control characters. Tenant &amp; Merchant stores it as an external reference and never keeps account data of its own.
/// </summary>
public sealed record SubjectId
{
    public const int MaxLength = 128;

    private SubjectId(string value) => Value = value;

    public string Value { get; }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out SubjectId? subjectId)
    {
        subjectId = value is { Length: > 0 and <= MaxLength } && !value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? new SubjectId(value)
            : null;
        return subjectId is not null;
    }

    public override string ToString() => Value;
}
