using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Gateway.IntegrationTests;

/// <summary>
/// The Gateway never forwards internal service-to-service endpoints (IDENTITY_API_CONTRACT.md section 2.12). The
/// catch-all route points at an unreachable destination, so a forwarded request answers <c>502</c> and a request the
/// Gateway refuses at the edge answers <c>404</c> without any forwarding attempt.
/// </summary>
public sealed class EdgeRoutingTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ReverseProxy:Routes:catch-all:ClusterId", "unreachable");
        builder.UseSetting("ReverseProxy:Routes:catch-all:Match:Path", "{**catch-all}");
        builder.UseSetting("ReverseProxy:Clusters:unreachable:Destinations:primary:Address", "http://127.0.0.1:1/");

        // A prefix-removing route, as any environment could configure: the outgoing path is checked after it.
        builder.UseSetting("ReverseProxy:Routes:prefixed:ClusterId", "unreachable");
        builder.UseSetting("ReverseProxy:Routes:prefixed:Match:Path", "/svc/{**catch-all}");
        builder.UseSetting("ReverseProxy:Routes:prefixed:Transforms:0:PathRemovePrefix", "/svc");

        // A rewriting route whose destination path is internal whatever the request path is.
        builder.UseSetting("ReverseProxy:Routes:rewritten:ClusterId", "unreachable");
        builder.UseSetting("ReverseProxy:Routes:rewritten:Match:Path", "/rewritten/{**rest}");
        builder.UseSetting("ReverseProxy:Routes:rewritten:Transforms:0:PathPattern", "/internal/v1/{**rest}");
    });

    [Theory]
    [InlineData("/internal/v1/accounts/123/disable")]
    [InlineData("/INTERNAL/v1/accounts/123/disable")]
    [InlineData("/internal/v1/tenants")]
    [InlineData("/Internal/V1/Tenants/0196f0a0-0000-7000-8000-000000000001")]
    [InlineData("/internal")]
    [InlineData("/svc/internal/v1/accounts/123/disable")]
    [InlineData("/svc/INTERNAL/v1/tenants")]
    [InlineData("/rewritten/accounts/123/disable")]
    public async Task InternalPaths_AreNotForwarded(string path)
    {
        using var client = _gateway.CreateClient();

        using var response = await client.PostAsync(path, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/auth/logout-all")]
    [InlineData("/api/v1/tenants")]
    [InlineData("/internalized/path")]
    [InlineData("/svc/api/v1/public")]
    public async Task OtherPaths_AreForwarded(string path)
    {
        using var client = _gateway.CreateClient();

        using var response = await client.PostAsync(path, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    public void Dispose() => _gateway.Dispose();
}
