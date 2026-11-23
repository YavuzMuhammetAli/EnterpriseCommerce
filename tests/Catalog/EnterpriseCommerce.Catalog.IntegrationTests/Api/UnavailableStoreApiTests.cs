using System.Net;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// MongoDB unavailable through the API: an authorized merchant request answers 503 with <c>Retry-After</c>, and the
/// problem carries no store details. No infrastructure is needed (nothing listens on the configured address).
/// </summary>
public sealed class UnavailableStoreApiTests(CatalogApiFixture fixture) : IClassFixture<CatalogApiFixture>
{
    [Fact]
    public async Task AuthorizedRequests_Answer503_WithoutDetails()
    {
        var tenantId = fixture.NewTenant(out var owner, out _);

        using var list = await fixture.SendAsync(owner, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantId}/products");
        using var create = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", CatalogApi.ProductBody("offline"), CatalogApi.Key());

        foreach (var response in new[] { list, create })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("5", response.Headers.RetryAfter?.ToString());
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("Mongo", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PublicDiscovery_Answers503()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/api/v1/catalog/products", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", response.Headers.RetryAfter?.ToString());
    }
}
