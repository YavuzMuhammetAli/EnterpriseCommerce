namespace EnterpriseCommerce.Identity.Api.Accounts;

/// <summary>
/// Fetch Metadata and exact <c>Origin</c> checks for state-changing requests to the Identity-hosted account pages and
/// the end-session page (ADR-016 section 6, ADR-017 section 9). They complement the antiforgery token, which Razor Pages validates on
/// every POST.
/// </summary>
internal static partial class CredentialFormProtection
{
    public const string AccountPathPrefix = "/account";

    /// <summary>The end-session page: its POST signs the browser out, so it gets the same protection.</summary>
    public const string EndSessionPath = "/" + Protocol.IdentityServerSetup.EndSessionPath;

    public static IApplicationBuilder UseCredentialFormProtection(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsProtectedPath(context.Request.Path))
            {
                // Account pages may carry credentials or single-use tokens (ADR-018 section 1).
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.XContentTypeOptions = "nosniff";
            }

            if (HttpMethods.IsPost(context.Request.Method)
                && IsProtectedPath(context.Request.Path)
                && !IsSameOrigin(context.Request))
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(CredentialFormProtection));
                LogRejected(logger, context.Request.Path);
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

    internal static bool IsProtectedPath(PathString path) =>
        path.StartsWithSegments(AccountPathPrefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments(EndSessionPath, StringComparison.OrdinalIgnoreCase);

    internal static bool IsSameOrigin(HttpRequest request)
    {
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0)
        {
            // same-site is not accepted: sibling subdomains are same-site but not same-origin.
            return string.Equals(fetchSite, "same-origin", StringComparison.Ordinal);
        }

        // Fallback for browsers without Fetch Metadata: an exact Origin match. Missing or "null" fails.
        var origin = request.Headers.Origin.ToString();
        return origin.Length > 0
            && string.Equals(origin, $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase);
    }

    [LoggerMessage(EventId = 5000, Level = LogLevel.Warning, Message = "Security event CsrfRejected: cross-origin POST to {RequestPath}")]
    private static partial void LogRejected(ILogger logger, PathString requestPath);
}
