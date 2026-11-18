using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Forwarded headers are applied before authentication and trusted only from the Gateway; host filtering rejects
/// foreign Host headers (ADR-017 section 10, PoC-14). Discovery is served by OpenIddict from the authentication
/// middleware, so its URLs show whether forwarding was applied in time.
/// </summary>
public sealed class TrustedProxyTests(TrustedProxyTests.ProxiedIdentityFactory factory) : IClassFixture<TrustedProxyTests.ProxiedIdentityFactory>
{
    private const string GatewayAddress = "10.0.0.5";
    private const string UntrustedAddress = "203.0.113.9";
    private const string PublicHost = "id.example.test";
    private const string InternalHost = "identity-internal";

    [Fact]
    public async Task ThroughTheGateway_DiscoveryUsesThePublicHttpsUrls()
    {
        using var response = await GetDiscoveryAsync(GatewayAddress, InternalHost, forwardedProto: "https", forwardedHost: PublicHost);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var discovery = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal($"https://{PublicHost}/", discovery.RootElement.GetProperty("issuer").GetString());
        Assert.Equal($"https://{PublicHost}/connect/authorize", discovery.RootElement.GetProperty("authorization_endpoint").GetString());
        Assert.Equal($"https://{PublicHost}/connect/token", discovery.RootElement.GetProperty("token_endpoint").GetString());
    }

    [Fact]
    public async Task DirectRequest_WithSpoofedForwardedHeaders_CannotInfluenceDiscovery()
    {
        using var response = await GetDiscoveryAsync(UntrustedAddress, InternalHost, forwardedProto: "https", forwardedHost: "evil.example");

        // Not trusted: the forwarded headers are ignored, so the request stays plain HTTP and OpenIddict refuses it.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("evil.example", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrustedGateway_ForwardingAnUnlistedHost_CannotInfluenceDiscovery()
    {
        using var response = await GetDiscoveryAsync(GatewayAddress, InternalHost, forwardedProto: "https", forwardedHost: "evil.example");

        Assert.DoesNotContain("evil.example", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignHostHeader_IsRejectedByHostFiltering()
    {
        using var response = await GetDiscoveryAsync(UntrustedAddress, "evil.example", forwardedProto: null, forwardedHost: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetDiscoveryAsync(string remoteAddress, string host, string? forwardedProto, string? forwardedHost)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{InternalHost}") });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        request.Headers.Host = host;
        request.Headers.Add(RemoteAddressStartupFilter.Header, remoteAddress);
        if (forwardedProto is not null)
        {
            request.Headers.Add("X-Forwarded-Proto", forwardedProto);
        }

        if (forwardedHost is not null)
        {
            request.Headers.Add("X-Forwarded-Host", forwardedHost);
        }

        request.Headers.Add("X-Forwarded-For", "198.51.100.20");
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Identity configured like a deployment behind the Gateway. The tests never open the database.</summary>
    public sealed class ProxiedIdentityFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder
                .UseEnvironment("Development")
                .UseSetting("ConnectionStrings:IdentityDatabase", "Server=tcp:127.0.0.1,1;Database=unused;Connect Timeout=1;TrustServerCertificate=true;Integrated Security=true")
                .UseSetting("Identity:Issuer", $"https://{PublicHost}/")
                .UseSetting("AllowedHosts", $"{InternalHost};{PublicHost}")
                .UseSetting("Identity:Proxy:KnownProxies:0", GatewayAddress)
                .UseSetting("Identity:Proxy:PublicHosts:0", PublicHost)
                .UseSetting("Serilog:MinimumLevel:Default", "Warning")
                .ConfigureTestServices(services => services.AddSingleton<IStartupFilter, RemoteAddressStartupFilter>());
    }

    /// <summary>
    /// TestServer has no network peer. This filter, which runs before the application's own middleware, sets the
    /// connection's remote address from a test-only header to simulate the Gateway or an untrusted client.
    /// </summary>
    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        public const string Header = "X-Test-Remote-Address";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[Header].ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
