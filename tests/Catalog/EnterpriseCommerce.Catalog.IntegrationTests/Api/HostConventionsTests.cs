using System.Net;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>Host conventions shared with the other services: liveness, the fallback policy and correlation IDs.</summary>
public sealed class HostConventionsTests(CatalogApiFixture fixture) : IClassFixture<CatalogApiFixture>
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    public async Task Liveness_IsAnonymousAndReportsTheStatusWordOnly(string path)
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnmappedPath_RequiresAuthentication()
    {
        using var client = fixture.CreateClient();
        using var response = await client.GetAsync("/api/v1/catalog/not-an-endpoint", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ASafeCorrelationId_IsEchoed_AndAnUnsafeOneIsReplaced()
    {
        using var client = fixture.CreateClient();
        using var safe = new HttpRequestMessage(HttpMethod.Get, "/health");
        safe.Headers.Add("X-Correlation-ID", "abc-123");
        using var unsafeRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
        unsafeRequest.Headers.TryAddWithoutValidation("X-Correlation-ID", "bad value with space");

        using var safeResponse = await client.SendAsync(safe, TestContext.Current.CancellationToken);
        using var unsafeResponse = await client.SendAsync(unsafeRequest, TestContext.Current.CancellationToken);

        Assert.Equal("abc-123", Assert.Single(safeResponse.Headers.GetValues("X-Correlation-ID")));
        Assert.True(Guid.TryParse(Assert.Single(unsafeResponse.Headers.GetValues("X-Correlation-ID")), out _));
    }
}
