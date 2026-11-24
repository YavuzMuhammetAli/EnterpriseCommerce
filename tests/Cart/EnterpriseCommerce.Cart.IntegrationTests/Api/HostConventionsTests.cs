using System.Net;
using Microsoft.AspNetCore.Hosting;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Cart.IntegrationTests.Api;

/// <summary>The host conventions (CART_OPERATIONS.md section 3, CART_API_CONTRACT.md section 1).</summary>
public sealed class HostConventionsTests(CartApiFixture fixture) : IClassFixture<CartApiFixture>
{
    [Fact]
    public async Task Liveness_NeedsNoDependency()
    {
        await using var factory = WithoutDatabase();
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/health", "/health/live" })
        {
            using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Readiness_FollowsTheDatabase_AndNeitherTheCacheNorCatalog()
    {
        fixture.RequireAvailable();
        fixture.Catalog.FailWith = HttpStatusCode.ServiceUnavailable;
        try
        {
            using var client = fixture.CreateClient();
            using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        finally
        {
            fixture.Catalog.FailWith = null;
        }

        await using var withoutDatabase = WithoutDatabase();
        using var unreachable = await withoutDatabase.CreateClient().GetAsync("/health/ready", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unreachable.StatusCode);
        Assert.Equal("Unhealthy", await unreachable.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheCacheAndCleanupChecks_HaveTheirOwnEndpoints()
    {
        using var client = fixture.CreateClient();

        using var cache = await client.GetAsync("/health/cache", TestContext.Current.CancellationToken);
        using var cleanup = await client.GetAsync("/health/cleanup", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, cache.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cleanup.StatusCode);
    }

    [Fact]
    public async Task ADatabaseOutage_Is503WithRetryAfter_WithoutDetails()
    {
        await using var factory = WithoutDatabase();
        using var client = CartApiFixture.CreateClient(factory, fixture.CustomerToken(CartApiFixture.NewCustomer()));

        using var response = await client.GetAsync("/api/v1/cart", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.RetryAfter?.ToString());
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("https://tools.ietf.org/html/rfc9110#section-15.6.4", body, StringComparison.Ordinal);
        foreach (var detail in new[] { "SqlException", "127.0.0.1", "Exception", "stack", "Server=" })
        {
            Assert.DoesNotContain(detail, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheCorrelationId_IsEchoed_AddedToProblems_AndPropagatedToCatalog()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
        var headers = new Dictionary<string, string> { ["X-Correlation-ID"] = "corr-cart-1" };

        using (var add = await fixture.SendAsync(customer, HttpMethod.Post, "/api/v1/cart/items", new { productId = product, variantId = variant.Id, quantity = 1 }, headers))
        {
            Assert.Equal("corr-cart-1", Assert.Single(add.Headers.GetValues("X-Correlation-ID")));
            Assert.Equal("corr-cart-1", fixture.Catalog.LastCorrelationId);
        }

        using var problem = await fixture.SendAsync(customer, HttpMethod.Delete, $"/api/v1/cart/items/{Guid.CreateVersion7()}", headers: headers);
        Assert.Equal("corr-cart-1", (await CartApiFixture.AssertProblemAsync(problem, HttpStatusCode.NotFound, "cart-item-not-found")).GetProperty("correlationId").GetString());

        using var unsafeId = await fixture.SendAsync(customer, HttpMethod.Get, "/api/v1/cart", headers: new Dictionary<string, string> { ["X-Correlation-ID"] = "bad id\r\n" });
        Assert.True(Guid.TryParse(Assert.Single(unsafeId.Headers.GetValues("X-Correlation-ID")), out _));
    }

    [Fact]
    public async Task AnUnknownPath_IsNotServedAnonymously()
    {
        using var client = fixture.CreateClient();

        using var internalPath = await client.GetAsync("/internal/v1/cart", TestContext.Current.CancellationToken);

        Assert.True(internalPath.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, internalPath.StatusCode.ToString());
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithoutDatabase() =>
        fixture.CreateFactory(builder => builder.UseSetting("ConnectionStrings:CartDatabase", CartApiFixture.UnreachableDatabase));
}
