/// <summary>
/// Internal service-to-service endpoints (<c>/internal/...</c>) are never reachable through the Gateway, on any host,
/// in any environment (ADR-007 edge concern; IDENTITY_API_CONTRACT.md section 2.12, TENANT_MERCHANT_API_CONTRACT.md
/// section 3). Services still authorize every internal call themselves; this rule never replaces that.
/// </summary>
/// <remarks>
/// <para>The rule is enforced twice, with the same check:</para>
/// <list type="number">
/// <item>on the incoming request path, before any route is selected;</item>
/// <item>
/// on the outgoing path of every proxied request, after the route's own transforms. A route that removes a prefix or
/// rewrites the path (for example <c>/identity/{**catch-all}</c> with <c>PathRemovePrefix</c>) can therefore never
/// produce an internal destination path.
/// </item>
/// </list>
/// <para>
/// A path counts as internal when its first segment is <c>internal</c> in any letter case, after percent-decoding
/// (repeated, to catch double encoding), treating <c>\</c> as a separator, and resolving empty, <c>.</c> and
/// <c>..</c> segments. Erring towards blocking is intended: no public route has a first segment that decodes to
/// <c>internal</c>.
/// </para>
/// </remarks>
internal static class InternalPaths
{
    public const string Segment = "internal";

    /// <summary>Upper bound for repeated percent-decoding, so a crafted path cannot make the check loop.</summary>
    private const int MaxDecodingPasses = 4;

    public static bool IsInternal(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var decoded = path;
        for (var pass = 0; pass < MaxDecodingPasses && decoded.Contains('%', StringComparison.Ordinal); pass++)
        {
            var next = Uri.UnescapeDataString(decoded);
            if (next == decoded)
            {
                break;
            }

            decoded = next;
        }

        var segments = new List<string>();
        foreach (var segment in decoded.Replace('\\', '/').Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return segments.Count > 0 && string.Equals(segments[0], Segment, StringComparison.OrdinalIgnoreCase);
    }
}
