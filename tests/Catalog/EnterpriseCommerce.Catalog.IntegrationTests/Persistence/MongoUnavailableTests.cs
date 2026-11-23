using System.Net;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Application.Products;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Persistence;

/// <summary>
/// MongoDB unavailable (no server listens): the host starts and stays live, readiness fails, and every store operation
/// fails as <see cref="DataStoreUnavailableException"/> (answered <c>503</c>). No infrastructure is needed.
/// </summary>
public sealed class MongoUnavailableTests(CatalogApiFixture fixture) : IClassFixture<CatalogApiFixture>
{
    [Fact]
    public async Task LivenessStaysHealthy_WhileReadinessFails()
    {
        using var client = fixture.CreateClient();

        using var live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsAndWrites_FailAsUnavailable()
    {
        var store = fixture.Factory.Services.GetRequiredService<IProductStore>();

        await Assert.ThrowsAsync<DataStoreUnavailableException>(() => store.GetAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<DataStoreUnavailableException>(() =>
            store.InsertAsync(CatalogSamples.Product(Guid.CreateVersion7(), "offline", "OFF-1"), TestContext.Current.CancellationToken));
    }
}
