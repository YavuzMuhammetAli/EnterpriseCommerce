using EnterpriseCommerce.Bff.Configuration;

namespace EnterpriseCommerce.Bff.Security;

/// <summary>
/// Return URLs are local paths only (ADR-016 section 2). Anything else, including protocol-relative URLs,
/// backslash tricks, control characters and encoded variants of them, becomes <c>/</c>.
/// </summary>
internal static class ReturnUrlValidator
{
    public const string Fallback = "/";
    private const int MaxLength = 2048;

    public static string Normalize(string? returnUrl) => IsLocal(returnUrl) ? returnUrl! : Fallback;

    public static bool IsLocal(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxLength || url[0] != '/')
        {
            return false;
        }

        // "//host" and "/\host" are protocol-relative for browsers.
        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
        {
            return false;
        }

        if (url.Any(c => char.IsControl(c) || c == '\\' || char.IsWhiteSpace(c)))
        {
            return false;
        }

        // Encoded slashes, backslashes or control characters could be decoded into "//" or "/\" by a later hop.
        string[] encoded = ["%2f", "%5c", "%09", "%0a", "%0d", "%00"];
        if (encoded.Any(sequence => url.Contains(sequence, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // The BFF's own protocol endpoints are never a destination.
        return !url.StartsWith(BffPaths.Prefix + "/", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(url, UriKind.Relative, out _);
    }
}
