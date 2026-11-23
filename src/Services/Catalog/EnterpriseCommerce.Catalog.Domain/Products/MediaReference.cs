using System.Diagnostics.CodeAnalysis;
using System.Net;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.Domain.Products;

/// <summary>
/// A reference to a product image hosted elsewhere (ADR-024 section 8). Catalog stores the reference and its metadata
/// only: it never stores binary media and never fetches the URL, so the URL cannot be used to reach internal
/// addresses from the server.
/// </summary>
public sealed record MediaReference
{
    public const int MaxUrlLength = 2048;
    public const int MaxAltTextLength = 250;

    /// <summary>The accepted image types.</summary>
    public static readonly IReadOnlySet<string> SupportedContentTypes =
        new HashSet<string>(StringComparer.Ordinal) { "image/jpeg", "image/png", "image/webp", "image/avif" };

    private static readonly string[] ReservedHostSuffixes = [".localhost", ".local", ".internal"];

    private MediaReference(Guid id, string url, string? altText, string contentType)
    {
        Id = id;
        Url = url;
        AltText = altText;
        ContentType = contentType;
    }

    public Guid Id { get; }

    public string Url { get; }

    public string? AltText { get; }

    public string ContentType { get; }

    public static bool TryCreate(
        Guid id,
        string? url,
        string? altText,
        string? contentType,
        [NotNullWhen(true)] out MediaReference? media,
        out ValidationErrors errors,
        string fieldPrefix = "")
    {
        errors = new ValidationErrors();
        if (!IsAcceptableUrl(url))
        {
            errors.Add(fieldPrefix + "url", $"The URL must be an absolute https URL of at most {MaxUrlLength} characters, with a public host name and no user information or fragment.");
        }

        if (!CatalogText.TryOptional(altText, MaxAltTextLength, multiLine: false, out var alt))
        {
            errors.Add(fieldPrefix + "altText", $"The alternative text is at most {MaxAltTextLength} characters on one line.");
        }

        if (contentType is null || !SupportedContentTypes.Contains(contentType))
        {
            errors.Add(fieldPrefix + "contentType", $"The content type must be one of {string.Join(", ", SupportedContentTypes)}.");
        }

        media = errors.IsEmpty ? new MediaReference(id, url!, alt, contentType!) : null;
        return media is not null;
    }

    /// <summary>Restores a stored reference, which was valid when it was written.</summary>
    public static MediaReference Rehydrate(Guid id, string url, string? altText, string contentType) => new(id, url, altText, contentType);

    /// <summary>
    /// An absolute <c>https</c> URL without user information or fragment, whose host is a DNS name: not an IP literal,
    /// not <c>localhost</c>, and not under <c>.localhost</c>, <c>.local</c> or <c>.internal</c>.
    /// </summary>
    public static bool IsAcceptableUrl([NotNullWhen(true)] string? url)
    {
        if (url is not { Length: > 0 and <= MaxUrlLength }
            || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0
            || url.Contains('#', StringComparison.Ordinal)
            || uri.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host.Length > 0
            && host.Contains('.', StringComparison.Ordinal)
            && host != "localhost"
            && !ReservedHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal))
            && !IPAddress.TryParse(host, out _);
    }
}
