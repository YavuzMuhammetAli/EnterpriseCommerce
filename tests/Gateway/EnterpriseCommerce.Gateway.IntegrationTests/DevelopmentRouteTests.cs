using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Gateway.IntegrationTests;

/// <summary>
/// The committed Development route set: no route forwards an internal path, and the Tenant &amp; Merchant route exposes
/// only the member API (TENANT_MERCHANT_API_CONTRACT.md section 5).
/// </summary>
public sealed class DevelopmentRouteTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseEnvironment("Development");

        // The Development cluster points at a local service that is not running in tests; send it nowhere instead.
        builder.UseSetting("ReverseProxy:Clusters:tenant-merchant:Destinations:primary:Address", "http://127.0.0.1:1/");
    });

    [Fact]
    public void NoDevelopmentRoute_MatchesAnInternalPath()
    {
        var internalRoutes = Routes()
            .Where(route => route.Path.TrimStart('/').StartsWith("internal", StringComparison.OrdinalIgnoreCase) || route.Path == "{**catch-all}" && route.Hosts.Count == 0)
            .Select(route => route.Name);

        Assert.Empty(internalRoutes);
    }

    [Fact]
    public void TheTenantMerchantRoute_ExposesOnlyTheMemberApi()
    {
        var route = Assert.Single(Routes(), candidate => candidate.Name == "tenant-merchant-api");

        Assert.Equal("/api/v1/tenants/{**catch-all}", route.Path);
        Assert.Equal(["localhost:7000"], route.Hosts);
    }

    [Fact]
    public void TheInvitationAcceptanceRoute_ExposesOnlyTheMerchantInvitationPaths()
    {
        // ADR-021: the invitee is not a member yet, so acceptance is not under /api/v1/tenants/{tenantId}.
        var route = Assert.Single(Routes(), candidate => candidate.Name == "tenant-merchant-invitations");

        Assert.Equal("/api/v1/merchant-invitations/{**catch-all}", route.Path);
        Assert.Equal(["localhost:7000"], route.Hosts);
        Assert.Equal(2, Routes().Count(candidate => candidate.Name.StartsWith("tenant-merchant", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("/internal/v1/tenants")]
    [InlineData("/internal/v1/tenants/0196f0a0-0000-7000-8000-000000000001")]
    public async Task InternalTenantPaths_AreRefusedOnTheTenantRouteHost(string path)
    {
        using var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7000") });

        using var response = await client.PostAsync(path, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheMemberApi_IsForwardedOnTheTenantRouteHost()
    {
        using var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7000") });

        using var response = await client.GetAsync("/api/v1/tenants/0196f0a0-0000-7000-8000-000000000001", TestContext.Current.CancellationToken);

        // Forwarded to the (unreachable) Tenant & Merchant destination.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    public void Dispose() => _gateway.Dispose();

    private static List<(string Name, string Path, IReadOnlyList<string> Hosts)> Routes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Gateway", "EnterpriseCommerce.Gateway", "appsettings.Development.json")));
        return [.. document.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").EnumerateObject().Select(route =>
        {
            var match = route.Value.GetProperty("Match");
            IReadOnlyList<string> hosts = match.TryGetProperty("Hosts", out var hostList) ? [.. hostList.EnumerateArray().Select(host => host.GetString()!)] : [];
            return (route.Name, match.GetProperty("Path").GetString()!, hosts);
        })];
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnterpriseCommerce.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
