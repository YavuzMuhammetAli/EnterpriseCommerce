namespace EnterpriseCommerce.Inventory.Domain.Common;

/// <summary>
/// The shape of a correlation ID and of a caller's reservation reference: 1 to 64 characters of
/// <c>A-Z a-z 0-9 - _ . :</c> (the <c>X-Correlation-ID</c> rule of ADR-023 section 4). Never personal data.
/// </summary>
public static class TraceText
{
    public const int MaxLength = 64;

    public static bool IsValid(string? value) =>
        value is { Length: >= 1 and <= MaxLength } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
}
