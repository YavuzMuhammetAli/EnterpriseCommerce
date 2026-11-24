using System.Net;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Api;

/// <summary>
/// The host conventions (INVENTORY_OPERATIONS.md section 3): liveness independent of the database, readiness on the
/// database and its migrations, the outbox check outside readiness, and <c>503</c> with <c>Retry-After</c> and no details
/// when SQL Server is unreachable.
/// </summary>
public sealed class HostConventionsTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithItsDatabase_TheServiceIsLiveAndReady_AndTheOutboxReportsItsStatusApart()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient();

        foreach (var path in new[] { "/health", "/health/live", "/health/ready" })
        {
            using var response = await client.GetAsync(path, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(Token));
        }

        using var outbox = await client.GetAsync("/health/outbox", Token);
        Assert.Equal(HttpStatusCode.OK, outbox.StatusCode);
    }

    [Fact]
    public async Task WithoutItsDatabase_TheServiceIsLiveButNotReady_AndRequestsAnswer503WithoutDetails()
    {
        await using var unreachable = fixture.CreateFactory(builder =>
            builder.UseSetting("ConnectionStrings:InventoryDatabase", "Server=tcp:127.0.0.1,1;Connect Timeout=1;TrustServerCertificate=true"));
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        using var client = InventoryApiFixture.CreateClient(unreachable, fixture.UserToken(owner));

        using var live = await client.GetAsync("/health/live", Token);
        using var ready = await client.GetAsync("/health/ready", Token);
        using var request = await client.GetAsync($"/api/v1/inventory/tenants/{tenantId}/items", Token);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal((HttpStatusCode.ServiceUnavailable, "Unhealthy"), (ready.StatusCode, await ready.Content.ReadAsStringAsync(Token)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, request.StatusCode);
        Assert.Equal("5", request.Headers.RetryAfter?.ToString());
        var body = await request.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointThatDoesNotExist_IsNotFound_AndUnknownInternalPathsNeedAToken()
    {
        using var client = fixture.CreateClient();

        using var internalPath = await client.GetAsync("/internal/v1/inventory/anything", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, internalPath.StatusCode);
    }
}
