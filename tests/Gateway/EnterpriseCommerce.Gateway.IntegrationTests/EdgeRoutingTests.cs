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
    });

    [Theory]
    [InlineData("/internal/v1/accounts/123/disable")]
    [InlineData("/INTERNAL/v1/accounts/123/disable")]
    [InlineData("/internal")]
    public async Task InternalPaths_AreNotForwarded(string path)
    {
        using var client = _gateway.CreateClient();

        using var response = await client.PostAsync(path, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/auth/logout-all")]
    [InlineData("/internalized/path")]
    public async Task OtherPaths_AreForwarded(string path)
    {
        using var client = _gateway.CreateClient();

        using var response = await client.PostAsync(path, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    public void Dispose() => _gateway.Dispose();
}
