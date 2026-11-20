using System.Net;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>
/// BFF liveness and readiness (Phase 2.3 S6.5): readiness follows the session store, liveness only the process, and
/// neither reveals more than the status word.
/// </summary>
public sealed class HealthTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task Readiness_FollowsTheSessionStore_WhileLivenessStaysHealthy()
    {
        environment.RequireAvailable();
        var (host, port) = TestInfrastructure.RedisEndpoint();
        await using var forwarder = new TcpForwarder(host, port);
        var bff = environment.CreateBff(environment.Storefront, options => options.RedisConnection = TestInfrastructure.RedisConnection(forwarder.Port));
        using var client = bff.Factory.CreateClient();

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await GetAsync(client, "/health/live"));
        Assert.Equal((HttpStatusCode.OK, "Healthy"), await GetAsync(client, "/health/ready"));

        forwarder.Cut();

        Assert.Equal((HttpStatusCode.OK, "Healthy"), await GetAsync(client, "/health/live"));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await GetAsync(client, "/health/ready"));
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), await GetAsync(client, "/health"));
    }

    private static async Task<(HttpStatusCode, string)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
