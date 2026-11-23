using System.Net;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// The relay's operational surface on the real host (ADR-023 section 6, PHASE_3_S4_EVENT_FLOW.md section 7): the
/// anonymous <c>/health/outbox</c> check, which never affects readiness or liveness. The startup validation of the
/// settings is tested through the host's own startup validator in <c>OutboxStartupValidationTests</c>.
/// </summary>
public sealed class OutboxOperationsTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    [Fact]
    public async Task TheOutboxCheck_IsAnonymous_AndHealthyWithoutAStaleBacklog()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync("/health/outbox", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStaleBacklog_DegradesTheOutboxCheckOnly_NeverReadinessOrLiveness()
    {
        fixture.RequireAvailable();
        var stale = await OutboxRows.InsertAsync(fixture.Database, Guid.CreateVersion7(), 1);
        await fixture.Database.ExecuteAsync($"UPDATE OutboxMessages SET OccurredAt = DATEADD(hour, -1, SYSDATETIMEOFFSET()) WHERE Sequence = {stale}");
        try
        {
            using var client = fixture.CreateClient();

            Assert.Equal("Degraded", await StatusAsync(client, "/health/outbox"));
            Assert.Equal("Healthy", await StatusAsync(client, "/health/ready"));
            Assert.Equal("Healthy", await StatusAsync(client, "/health/live"));
        }
        finally
        {
            await fixture.Database.ExecuteAsync($"UPDATE OutboxMessages SET PublishedAt = SYSDATETIMEOFFSET() WHERE Sequence = {stale}");
        }
    }

    private static async Task<string> StatusAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
