using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>CSRF layers, CORS and API forwarding (ADR-016 sections 2 and 6).</summary>
public sealed class BrowserProtectionTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task StateChangingRequests_NeedSameOriginAndASessionBoundToken()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        using var otherBrowser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        await environment.SignInAsync(browser, bff, email);
        await environment.SignInAsync(otherBrowser, bff, email);
        var (_, xsrf) = await BffTestEnvironment.UserAsync(browser, bff);
        var (_, otherXsrf) = await BffTestEnvironment.UserAsync(otherBrowser, bff);
        var logout = bff.Url("/bff/logout");

        var attempts = new (string Name, Func<Task<BrowserResponse>> Send)[]
        {
            ("no fetch metadata, no origin", () => browser.SendAsync(new HttpRequestMessage(HttpMethod.Post, logout) { Headers = { { "X-XSRF-TOKEN", xsrf } } })),
            ("cross-site", () => browser.FetchAsync(HttpMethod.Post, logout, xsrf, fetchSite: "cross-site", origin: "https://evil.example.test")),
            ("same-site sibling", () => browser.FetchAsync(HttpMethod.Post, logout, xsrf, fetchSite: "same-site", origin: "https://merchant.example.test")),
            ("foreign origin without fetch metadata", () => browser.FetchAsync(HttpMethod.Post, logout, xsrf, fetchSite: "", origin: "https://evil.example.test")),
            ("null origin", () => browser.FetchAsync(HttpMethod.Post, logout, xsrf, fetchSite: "", origin: "null")),
            ("same origin without token", () => browser.FetchAsync(HttpMethod.Post, logout, xsrfToken: null)),
            ("same origin with a garbage token", () => browser.FetchAsync(HttpMethod.Post, logout, "garbage")),
            ("same origin with another session's token", () => browser.FetchAsync(HttpMethod.Post, logout, otherXsrf)),
        };

        foreach (var (name, send) in attempts)
        {
            var response = await send();
            Assert.True(response.Status == HttpStatusCode.Forbidden, $"{name}: {response.Status}");
            Assert.NotNull(browser.Cookie(bff.Origin, bff.SessionCookieName));
        }

        // Without Fetch Metadata, an exact Origin is accepted (older browsers).
        var accepted = await browser.FetchAsync(HttpMethod.Post, logout, xsrf, fetchSite: "", origin: bff.Origin.GetLeftPart(UriPartial.Authority));
        Assert.Equal(HttpStatusCode.OK, accepted.Status);
        Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
    }

    [Fact]
    public async Task NoCorsHeaderIsEverSent()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());

        var preflight = new HttpRequestMessage(HttpMethod.Options, bff.Url("/bff/logout"));
        preflight.Headers.Add("Origin", "https://evil.example.test");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "X-XSRF-TOKEN");
        var responses = new[]
        {
            await browser.SendAsync(preflight),
            await browser.GetAsync(bff.Url("/bff/user"), request => request.Headers.Add("Origin", "https://evil.example.test")),
        };

        Assert.All(responses, response => Assert.DoesNotContain(response.Headers.Keys, key => key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ApiCalls_AreForwardedWithTheSessionBearerToken_AndNothingOfTheBrowser()
    {
        environment.RequireAvailable();
        await using var downstream = await Downstream.StartAsync();
        var bff = environment.CreateBff(environment.Storefront, options =>
        {
            options.DownstreamApiAddress = "https://gateway.example.test/";
            options.DownstreamHandler = downstream.Handler;
        });
        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, await environment.CreateUserAsync());
        var (_, xsrf) = await BffTestEnvironment.UserAsync(browser, bff);

        var get = await browser.FetchAsync(HttpMethod.Get, bff.Url("/api/orders?page=2"), xsrfToken: null);
        Assert.Equal(HttpStatusCode.OK, get.Status);
        using (var echo = JsonDocument.Parse(get.Body))
        {
            Assert.Equal("/api/orders", echo.RootElement.GetProperty("path").GetString());
            Assert.Equal("ec-storefront", echo.RootElement.GetProperty("audience").GetString());
            Assert.False(echo.RootElement.GetProperty("cookie").GetBoolean());
            Assert.False(echo.RootElement.GetProperty("xsrf").GetBoolean());
        }

        Assert.Null(get.SetCookie("downstream"));
        Assert.Equal("no-store", get.Header("Cache-Control"));

        // A state-changing call without the antiforgery token is not forwarded at all.
        var before = downstream.Requests;
        var forged = await browser.FetchAsync(HttpMethod.Post, bff.Url("/api/orders"), xsrfToken: null);
        Assert.Equal(HttpStatusCode.Forbidden, forged.Status);
        Assert.Equal(before, downstream.Requests);

        var post = await browser.FetchAsync(HttpMethod.Post, bff.Url("/api/orders"), xsrf);
        Assert.Equal(HttpStatusCode.OK, post.Status);
        Assert.Equal(before + 1, downstream.Requests);

        // Without a session nothing is forwarded either.
        using var anonymous = environment.CreateBrowser(bff);
        var unauthenticated = await anonymous.FetchAsync(HttpMethod.Get, bff.Url("/api/orders"), xsrfToken: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.Status);
        Assert.Equal(before + 1, downstream.Requests);
    }

    /// <summary>A stand-in for the Gateway: echoes what it received.</summary>
    private sealed class Downstream : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _requests;

        private Downstream(WebApplication app) => _app = app;

        public int Requests => Volatile.Read(ref _requests);

        public HttpMessageHandler Handler => _app.GetTestServer().CreateHandler();

        public static async Task<Downstream> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            var app = builder.Build();
            Downstream? self = null;
            app.Run(async context =>
            {
                Interlocked.Increment(ref self!._requests);
                var authorization = context.Request.Headers.Authorization.ToString();
                var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? new JsonWebToken(authorization["Bearer ".Length..]) : null;
                context.Response.Headers.Append("Set-Cookie", "downstream=1; Path=/");
                await context.Response.WriteAsJsonAsync(new
                {
                    path = context.Request.Path.Value,
                    audience = token?.Audiences.FirstOrDefault(),
                    cookie = context.Request.Headers.Cookie.Count > 0,
                    xsrf = context.Request.Headers.ContainsKey("X-XSRF-TOKEN"),
                });
            });
            self = new Downstream(app);
            await app.StartAsync();
            return self;
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
