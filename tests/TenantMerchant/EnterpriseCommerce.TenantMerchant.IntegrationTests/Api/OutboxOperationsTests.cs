using System.Net;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// The relay's operational surface on the real host (ADR-023 section 6, PHASE_3_S4_EVENT_FLOW.md section 7): the
/// anonymous <c>/health/outbox</c> check, which never affects readiness or liveness, and the startup validation of the
/// outbox and Kafka settings.
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

    [Theory]
    [InlineData("TenantMerchant:Outbox:BatchSize", "0", "TenantMerchant:Outbox is invalid")]
    [InlineData("TenantMerchant:Outbox:Topic", "Tenant_Events", "TenantMerchant:Outbox is invalid")]
    [InlineData("TenantMerchant:Kafka:MessageTimeout", "00:10:00", "TenantMerchant:Kafka is invalid")]
    public async Task InvalidRelaySettings_StopTheHostFromStarting(string setting, string value, string expected)
    {
        await using var factory = fixture.CreateFactory(
            "Server=tcp:127.0.0.1,1;Database=unused;TrustServerCertificate=true",
            builder =>
            {
                builder.UseSetting("TenantMerchant:Kafka:BootstrapServers", "127.0.0.1:1");
                builder.UseSetting(setting, value);
            });

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains(expected, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("broker:9092", null)]
    [InlineData("broker:9093", "Ssl")]
    public async Task OutsideDevelopment_TheHostRefusesToStart_WithoutAMutualTlsBroker(string? bootstrapServers, string? protocol)
    {
        await using var factory = fixture.CreateFactory(
            "Server=tcp:127.0.0.1,1;Database=unused;TrustServerCertificate=true",
            builder =>
            {
                builder.UseEnvironment("Production");
                if (bootstrapServers is not null)
                {
                    builder.UseSetting("TenantMerchant:Kafka:BootstrapServers", bootstrapServers);
                }

                if (protocol is not null)
                {
                    builder.UseSetting("TenantMerchant:Kafka:SecurityProtocol", protocol);
                }
            });

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("TenantMerchant:Kafka is invalid", failure.ToString(), StringComparison.Ordinal);
    }

    private static async Task<string> StatusAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }
}
