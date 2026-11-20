using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Logging;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Bff.Security;

/// <summary>
/// CSRF defence for state-changing requests to <c>/bff/*</c> and <c>/api/*</c> (ADR-016 section 6). All layers must
/// pass, otherwise 403 before anything is executed or forwarded:
/// <list type="number">
/// <item>The SameSite=Strict session cookie (set by <see cref="BffSessionHandler"/>).</item>
/// <item>Fetch Metadata: <c>Sec-Fetch-Site: same-origin</c>; without Fetch Metadata an exact <c>Origin</c> match with
/// the deployment's public origin. A missing or <c>null</c> origin fails; <c>same-site</c> fails.</item>
/// <item>A session-bound antiforgery token in <c>X-XSRF-TOKEN</c>.</item>
/// <item>No CORS: the BFF registers no CORS policy, so no <c>Access-Control-Allow-*</c> header is ever sent.</item>
/// </list>
/// </summary>
internal static class BrowserRequestProtection
{
    public const string HeaderName = "X-XSRF-TOKEN";

    public static IApplicationBuilder UseBrowserRequestProtection(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (!IsProtected(context.Request))
            {
                await next(context);
                return;
            }

            var settings = context.RequestServices.GetRequiredService<IOptions<BffSettings>>().Value;
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(BrowserRequestProtection));
            if (!IsSameOrigin(context.Request, settings.PublicOrigin))
            {
                BffLog.CsrfRejected(logger, settings.Deployment, context.Request.Path, "origin");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // Antiforgery tokens are bound to the session; without a session there is nothing to protect.
            var authentication = await context.AuthenticateAsync(BffSessionDefaults.Scheme);
            if (!authentication.Succeeded)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = BffSessionHandler.LoginHint;
                return;
            }

            context.User = authentication.Principal;
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                BffLog.CsrfRejected(logger, settings.Deployment, context.Request.Path, "antiforgery");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });

    internal static bool IsProtected(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        && (request.Path.StartsWithSegments(BffPaths.Prefix, StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments(BffPaths.Api, StringComparison.OrdinalIgnoreCase));

    internal static bool IsSameOrigin(HttpRequest request, string publicOrigin)
    {
        var origin = request.Headers.Origin.ToString();
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0)
        {
            // same-site is not accepted: a sibling subdomain (another front end) is same-site but not same-origin.
            return string.Equals(fetchSite, "same-origin", StringComparison.Ordinal)
                && (origin.Length == 0 || string.Equals(origin, publicOrigin, StringComparison.OrdinalIgnoreCase));
        }

        return origin.Length > 0 && string.Equals(origin, publicOrigin, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Binds antiforgery tokens to the session record, not only to the user: a new session needs a new token.</summary>
internal sealed class SessionBoundAntiforgeryData : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context) =>
        context.User.FindFirst(BffClaims.SessionRecord)?.Value is { } recordId ? SessionIds.LogReference(recordId) : string.Empty;

    public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
        additionalData.Length > 0
        && context.User.FindFirst(BffClaims.SessionRecord)?.Value is { } recordId
        && string.Equals(additionalData, SessionIds.LogReference(recordId), StringComparison.Ordinal);
}
