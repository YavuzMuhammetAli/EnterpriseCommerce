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
        builder.UseSetting("ReverseProxy:Clusters:catalog:Destinations:primary:Address", "http://127.0.0.1:1/");
        builder.UseSetting("ReverseProxy:Clusters:inventory:Destinations:primary:Address", "http://127.0.0.1:1/");
        builder.UseSetting("ReverseProxy:Clusters:cart:Destinations:primary:Address", "http://127.0.0.1:1/");
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

    [Fact]
    public void TheCatalogRoute_ExposesOnlyTheCatalogApi()
    {
        // ADR-024: public discovery and the merchant API share /api/v1/catalog; the taxonomy API is internal only.
        var route = Assert.Single(Routes(), candidate => candidate.Name.StartsWith("catalog", StringComparison.Ordinal));

        Assert.Equal("catalog-api", route.Name);
        Assert.Equal("/api/v1/catalog/{**catch-all}", route.Path);
        Assert.Equal(["localhost:7000"], route.Hosts);
    }

    [Fact]
    public void TheInventoryRoute_ExposesOnlyTheMerchantInventoryApi()
    {
        // ADR-026: the merchant API is public behind the Gateway; the reservation API is internal only.
        var route = Assert.Single(Routes(), candidate => candidate.Name.StartsWith("inventory", StringComparison.Ordinal));

        Assert.Equal("inventory-api", route.Name);
        Assert.Equal("/api/v1/inventory/{**catch-all}", route.Path);
        Assert.Equal(["localhost:7000"], route.Hosts);
    }

    [Fact]
    public void TheCartRoute_ExposesOnlyTheCustomerCartApi()
    {
        // ADR-029: the storefront customer's cart; Cart has no internal endpoints, and none may become reachable.
        var route = Assert.Single(Routes(), candidate => candidate.Name.StartsWith("cart", StringComparison.Ordinal));

        Assert.Equal("cart-api", route.Name);
        Assert.Equal("/api/v1/cart/{**catch-all}", route.Path);
        Assert.Equal(["localhost:7000"], route.Hosts);
    }

    [Theory]
    [InlineData("GET", "/api/v1/cart")]
    [InlineData("POST", "/api/v1/cart/items")]
    [InlineData("PUT", "/api/v1/cart/items/0196f0a0-0000-7000-8000-000000000002")]
    [InlineData("DELETE", "/api/v1/cart/items")]
    [InlineData("POST", "/api/v1/cart/refresh")]
    public async Task TheCartApi_IsForwardedOnTheServiceRouteHost(string method, string path)
    {
        using var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7000") });

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), TestContext.Current.CancellationToken);

        // Forwarded to the (unreachable) Cart destination.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/carts")]
    [InlineData("/api/v1/cartx/items")]
    public async Task OnlyTheCartPrefix_IsRouted(string path)
    {
        using var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7000") });

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/internal/v1/inventory/tenants/0196f0a0-0000-7000-8000-000000000001/reservations")]
    [InlineData("/internal/v1/inventory/tenants/0196f0a0-0000-7000-8000-000000000001/reservations/0196f0a0-0000-7000-8000-000000000002/commit")]
    [InlineData("/internal/v1/catalog/tenants/0196f0a0-0000-7000-8000-000000000001/variant-verifications")]
    public async Task InternalInventoryAndVerificationPaths_AreRefusedOnTheServiceRouteHost(string path)
    {
        using var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7000") });

        using var response = await client.PostAsync(path, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/inventory/tenants/0196f0a0-0000-7000-8000-000000000001/items")]
    [InlineData("/api/v1/inventory/tenants/0196f0a0-0000-7000-8000-000000000001/items/0196f0a0-0000-7000-8000-000000000002/movements")]
    public async Task TheInventoryApi_IsForwardedOnTheServiceRouteHost(string path)
    {
        using var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7000") });

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // Forwarded to the (unreachable) Inventory destination.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Theory]
    [InlineData("/internal/v1/catalog/categories")]
    [InlineData("/internal/v1/catalog/categories/0196f0a0-0000-7000-8000-000000000001/retire")]
    public async Task InternalCatalogPaths_AreRefusedOnTheCatalogRouteHost(string path)
    {
        using var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7000") });

        using var response = await client.PostAsync(path, content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/catalog/products")]
    [InlineData("/api/v1/catalog/tenants/0196f0a0-0000-7000-8000-000000000001/products")]
    public async Task TheCatalogApi_IsForwardedOnTheCatalogRouteHost(string path)
    {
        using var client = _gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost:7000") });

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // Forwarded to the (unreachable) Catalog destination.
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
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
