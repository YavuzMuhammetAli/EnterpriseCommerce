namespace EnterpriseCommerce.Catalog.Application.Common;

/// <summary>
/// A search text for the MongoDB text index (ADR-024 section 9). The characters <c>-</c> and <c>"</c> are removed, so
/// a query can never express negation or phrases; control characters are refused. The result has 2 to 100 characters
/// and at most 10 terms. Terms match whole words, combined with OR, without stemming.
/// </summary>
public static class SearchText
{
    public const int MinLength = 2;
    public const int MaxLength = 100;
    public const int MaxTerms = 10;

    public const string Rule = "2 to 100 characters and at most 10 words; '-' and '\"' are ignored.";

    /// <summary>Validates an optional search text. <c>null</c> or empty means no search.</summary>
    public static bool TryCreate(string? value, out string? text)
    {
        text = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (value.Length > MaxLength * 2 || value.Any(char.IsControl))
        {
            return false;
        }

        var terms = value.Replace("-", " ", StringComparison.Ordinal).Replace("\"", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(' ', terms);
        if (joined.Length is < MinLength or > MaxLength || terms.Length > MaxTerms)
        {
            return false;
        }

        text = joined;
        return true;
    }
}
