using System.Net.Http.Headers;
using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.AspNetCore.Authentication;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

namespace EnterpriseCommerce.Bff.Proxy;

/// <summary>
/// Forwards <c>/api/*</c> to the Gateway with the session's access token as a bearer token (ADR-016 section 2).
/// The browser's cookies, antiforgery header and any <c>Authorization</c> header are removed; downstream
/// <c>Set-Cookie</c> headers never reach the browser. Authorization stays with the downstream services.
/// </summary>
internal static class ApiForwarding
{
    private const string AccessTokenItem = "bff.access-token";

    public static IServiceCollection AddApiForwarding(this IServiceCollection services, BffSettings settings)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        if (!string.IsNullOrWhiteSpace(settings.DownstreamApiAddress))
        {
            routes.Add(new RouteConfig { RouteId = "api", ClusterId = "downstream", Match = new RouteMatch { Path = BffPaths.Api + "/{**catch-all}" } });
            clusters.Add(new ClusterConfig
            {
                ClusterId = "downstream",
                Destinations = new Dictionary<string, DestinationConfig> { ["gateway"] = new() { Address = settings.DownstreamApiAddress } },
            });
        }

        services.AddReverseProxy()
            .LoadFromMemory(routes, clusters)
            .AddTransforms(transforms =>
            {
                transforms.AddRequestTransform(context =>
                {
                    var headers = context.ProxyRequest.Headers;
                    headers.Remove("Cookie");
                    headers.Remove(Security.BrowserRequestProtection.HeaderName);
                    headers.Authorization = new AuthenticationHeaderValue("Bearer", (string)context.HttpContext.Items[AccessTokenItem]!);
                    return ValueTask.CompletedTask;
                });
                transforms.AddResponseTransform(context =>
                {
                    context.HttpContext.Response.Headers.Remove("Set-Cookie");
                    return ValueTask.CompletedTask;
                });
            });

        return services;
    }

    /// <summary>Authenticates the session and obtains an access token (refreshing if due) before anything is forwarded.</summary>
    public static IEndpointConventionBuilder MapApiForwarding(this IEndpointRouteBuilder app) =>
        app.MapReverseProxy(pipeline => pipeline.Use(async (context, next) =>
        {
            var authentication = await context.AuthenticateAsync(BffSessionDefaults.Scheme);
            var session = authentication.Succeeded ? context.Features.Get<BffSessionFeature>()?.Session : null;
            if (session is null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = BffSessionHandler.LoginHint;
                return;
            }

            var coordinator = context.RequestServices.GetRequiredService<RefreshCoordinator>();
            var token = await coordinator.GetAccessTokenAsync(session, context.RequestAborted);
            switch (token.Status)
            {
                case AccessTokenStatus.SessionEnded:
                    await context.SignOutAsync(BffSessionDefaults.Scheme);
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = BffSessionHandler.LoginHint;
                    return;
                case AccessTokenStatus.Unavailable:
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    context.Response.Headers.RetryAfter = "2";
                    return;
            }

            context.Items[AccessTokenItem] = token.AccessToken;
            await next();
        }));
}
