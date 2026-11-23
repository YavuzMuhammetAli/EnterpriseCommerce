using System.Text.Json;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Kafka;

/// <summary>
/// A real broker outage (ADR-023 section 6, PHASE_3_S4_EVENT_FLOW.md section 8): the broker's container is stopped and
/// started again, as the Redis restart test does. A TCP forwarder cannot simulate it, because the client reconnects to
/// the advertised listener past it. It runs alone, after the parallel tests, because it stops the shared broker.
/// </summary>
[Collection(nameof(KafkaOutageCollection))]
public sealed class KafkaOutageTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    [Fact]
    public async Task WhileKafkaIsDown_CreationsSucceed_AttemptsGrow_HealthDegrades_AndTheBacklogDrainsInOrderAfterwards()
    {
        var container = RequireRestartableBroker();
        var (database, topic) = await KafkaPublicationTests.ArrangeAsync();
        await using var _ = database;
        await using var __ = topic;
        await using var factory = new KafkaPublicationTests(fixture).KafkaHost(database, topic.Name, builder =>
        {
            // Short bounds keep the test fast; the behaviour is the same with the defaults.
            builder.UseSetting("TenantMerchant:Kafka:MessageTimeout", "00:00:03");
            builder.UseSetting("TenantMerchant:Outbox:MaxBackoff", "00:00:02");
        });
        using var health = KafkaPublicationTests.WebApplicationFactoryClient(factory);

        var before = await KafkaPublicationTests.CreateAsync(factory, "/api/v1/tenants", fixture.UserToken("user-outage-0"), new { slug = "outage-before", displayName = "Before" }, "corr-outage-0");
        await Eventually.TrueAsync(async () => (await OutboxRows.TenantCreatedAsync(database, before))[0].PublishedAt is not null, Deadline, "the first event published");

        var during = new List<Guid>();
        await KafkaTestEnvironment.DockerAsync("stop", container);
        try
        {
            // Business requests never need Kafka.
            for (var i = 1; i <= 3; i++)
            {
                during.Add(await KafkaPublicationTests.CreateAsync(factory, "/api/v1/tenants", fixture.UserToken($"user-outage-{i}"), new { slug = $"outage-during-{i}", displayName = $"During {i}" }, $"corr-outage-{i}"));
            }

            await Eventually.TrueAsync(
                async () => (await OutboxRows.TenantCreatedAsync(database, during[0])).Single() is { Attempts: >= 2, PublishedAt: null },
                Deadline,
                "failed attempts recorded on the head row");
            await Eventually.TrueAsync(
                async () => await (await health.GetAsync("/health/outbox", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken) == "Degraded",
                Deadline,
                "the outbox check reports Degraded");
            Assert.All(await OutboxRows.ReadAsync(database), row => Assert.True(row.MessageKey == before.ToString("D") || row.PublishedAt is null));
            using var ready = await health.GetAsync("/health/ready", TestContext.Current.CancellationToken);
            Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            await KafkaTestEnvironment.DockerAsync("start", container);
        }

        await Eventually.TrueAsync(async () => (await OutboxRows.ReadAsync(database)).All(row => row.PublishedAt is not null), Deadline, "the backlog drained");

        // Drained in commit order: one at a time, in Sequence order.
        var rows = await OutboxRows.ReadAsync(database);
        Assert.Equal(rows.OrderBy(row => row.Sequence).Select(row => row.Sequence), rows.OrderBy(row => row.PublishedAt).ThenBy(row => row.Sequence).Select(row => row.Sequence));
        Assert.Equal("Healthy", await (await health.GetAsync("/health/outbox", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var messages = topic.ReadAtLeast(4, Deadline);
        Assert.Equal(
            new[] { before }.Concat(during).Select(id => id.ToString("D")).Order(),
            messages.Select(message => message.Message.Key).Distinct().Order());
        Assert.All(messages, message =>
        {
            using var envelope = JsonDocument.Parse(message.Message.Value);
            Assert.Equal(message.Message.Key, envelope.RootElement.GetProperty("tenantId").GetString());
        });
    }

    private static string RequireRestartableBroker()
    {
        KafkaTestEnvironment.RequireAvailable();
        if (Environment.GetEnvironmentVariable(KafkaTestEnvironment.RestartContainerVariable) is { Length: > 0 } container)
        {
            return container;
        }

        var message = $"Kafka outage test: set {KafkaTestEnvironment.RestartContainerVariable} to the broker's container name (enterprisecommerce-kafka-1 with Compose).";
        if (string.Equals(Environment.GetEnvironmentVariable(SqlServerTestDatabase.RequireVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail(message);
        }

        Assert.Skip(message);
        return string.Empty;
    }
}

/// <summary>Stops the shared broker: runs alone, after the parallel tests.</summary>
[CollectionDefinition(nameof(KafkaOutageCollection), DisableParallelization = true)]
public sealed class KafkaOutageCollection;
