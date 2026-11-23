using System.Globalization;

namespace EnterpriseCommerce.Catalog.Api.Common;

/// <summary>
/// Optimistic concurrency over HTTP (ADR-024 section 14): reads return the version as a strong entity tag, and every
/// full replacement requires it in <c>If-Match</c>. A missing header answers <c>428</c>; a malformed, weak or wildcard
/// one cannot match a version and answers <c>412</c>.
/// </summary>
internal static class EntityTags
{
    public static string For(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    /// <summary>Writes the entity tag of <paramref name="version"/> on the response.</summary>
    public static void Set(HttpContext httpContext, long version) => httpContext.Response.Headers.ETag = For(version);

    /// <summary>
    /// <c>null</c> when the header is missing (answer <c>428</c>); otherwise the version, or <c>-1</c> when the header is
    /// not exactly one strong tag of a version (answer <c>412</c>).
    /// </summary>
    public static long? ExpectedVersion(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.IfMatch is not [{ } value])
        {
            return request.Headers.IfMatch.Count == 0 ? null : -1;
        }

        var tag = value.Trim();
        return tag.Length > 2 && tag[0] == '"' && tag[^1] == '"'
            && long.TryParse(tag.AsSpan(1, tag.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var version)
                ? version
                : -1;
    }
}
