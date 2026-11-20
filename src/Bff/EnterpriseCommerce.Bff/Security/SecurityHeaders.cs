using EnterpriseCommerce.Bff.Configuration;

namespace EnterpriseCommerce.Bff.Security;

/// <summary>Response headers for every BFF response, plus no-store for the protocol and API endpoints.</summary>
internal static class SecurityHeaders
{
    public static IApplicationBuilder UseBffSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers.XFrameOptions = "DENY";
                headers["Cross-Origin-Opener-Policy"] = "same-origin";

                if (context.Request.Path.StartsWithSegments(BffPaths.Prefix, StringComparison.OrdinalIgnoreCase)
                    || context.Request.Path.StartsWithSegments(BffPaths.Api, StringComparison.OrdinalIgnoreCase))
                {
                    headers.CacheControl = "no-store";
                    headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            });

            return next(context);
        });
}
