namespace EnterpriseCommerce.Catalog.Domain.Common;

/// <summary>
/// Text rules of catalog content. Values are trimmed and never otherwise normalized. Single-line values contain no
/// control characters; multi-line values allow line breaks and tabs only.
/// </summary>
public static class CatalogText
{
    /// <summary>A required single-line value of 1 to <paramref name="maxLength"/> characters after trimming.</summary>
    public static bool TryRequired(string? value, int maxLength, out string text)
    {
        text = value?.Trim() ?? string.Empty;
        return text.Length is > 0 && text.Length <= maxLength && !text.Any(char.IsControl);
    }

    /// <summary>
    /// An optional value of at most <paramref name="maxLength"/> characters after trimming. White space only means no
    /// value. <paramref name="multiLine"/> allows line breaks and tabs.
    /// </summary>
    public static bool TryOptional(string? value, int maxLength, bool multiLine, out string? text)
    {
        var trimmed = value?.Trim();
        text = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        return text is null
            || (text.Length <= maxLength && text.All(c => !char.IsControl(c) || (multiLine && c is '\n' or '\r' or '\t')));
    }
}
