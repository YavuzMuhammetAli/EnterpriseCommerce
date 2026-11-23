using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Kafka;

/// <summary>
/// The outbox relay against a real Kafka broker and SQL Server (ADR-023 sections 4 to 6, PHASE_3_S4_EVENT_FLOW.md
/// section 8): end to end from both creation endpoints, per-tenant order, dual-fact identity, at-least-once redelivery
/// with identical bytes, recovery by a new host, graceful shutdown, and a missing topic. Every test has its own database
/// and its own topic.
/// </summary>
public sealed class KafkaPublicationTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task BothCreationEndpoints_PublishTenantCreated_KeyedByTheTenant_WithMatchingHeaders_AndASchemaValidBody()
    {
        var (database, topic) = await ArrangeAsync();
        await using var _ = database;
        await using var __ = topic;
        await using var factory = KafkaHost(database, topic.Name);

        var provisioned = await CreateAsync(factory, "/internal/v1/tenants", fixture.AdminToken(), new { slug = "kafka-provisioned", displayName = "Kafka Provisioned", ownerSubjectId = "user-kafka-1", actorReference = "admin:kafka" }, "corr-kafka-provisioned");
        var selfService = await CreateAsync(factory, "/api/v1/tenants", fixture.UserToken("user-kafka-2"), new { slug = "kafka-self-service", displayName = "Kafka Self Service" }, "corr-kafka-self");

        var messages = topic.ReadAtLeast(2, Deadline);
        await Eventually.TrueAsync(async () => (await OutboxRows.ReadAsync(database)).All(row => row.PublishedAt is not null), Deadline, "every row marked published");

        var rows = await OutboxRows.ReadAsync(database);
        Assert.Equal(2, messages.Count);
        foreach (var (tenantId, origin, correlation) in new[] { (provisioned, "Provisioned", "corr-kafka-provisioned"), (selfService, "SelfService", "corr-kafka-self") })
        {
            var message = Assert.Single(messages, candidate => candidate.Message.Key == tenantId.ToString("D"));
            var row = Assert.Single(rows, candidate => candidate.MessageKey == tenantId.ToString("D"));
            var body = Encoding.UTF8.GetString(message.Message.Value);

            Assert.Equal(row.Envelope, body);
            EventSchemas.AssertValidTenantCreatedV1(body);
            using var envelope = JsonDocument.Parse(body);
            Assert.Equal(origin, envelope.RootElement.GetProperty("payload").GetProperty("origin").GetString());
            Assert.Equal(
                [("event-id", row.EventId.ToString("D")), ("event-type", "TenantCreated"), ("event-version", "1"), ("correlation-id", correlation), ("content-type", "application/json")],
                Headers(message));
            Assert.Equal(correlation, envelope.RootElement.GetProperty("correlationId").GetString());
        }
    }

    [Fact]
    public async Task OneTenantsMessages_ArriveInCommitOrder_OnOnePartition_AndTenantsSpreadOverThePartitions()
    {
        var (database, topic) = await ArrangeAsync();
        await using var _ = database;
        await using var __ = topic;
        var tenant = Guid.CreateVersion7().ToString("D");
        var ordered = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            var eventId = Guid.CreateVersion7();
            ordered.Add(eventId);
            await OutboxRows.InsertAsync(database, eventId, 1, tenant, topic.Name);
            await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1, Guid.CreateVersion7().ToString("D"), topic.Name);
        }

        await using var relay = KafkaRelay(database, topic.Name);
        await DrainAsync(relay, database);

        var messages = topic.ReadAtLeast(40, Deadline);
        var ofTenant = messages.Where(message => message.Message.Key == tenant).ToList();
        Assert.Single(ofTenant.Select(message => message.Partition.Value).Distinct());
        Assert.Equal(ordered, ofTenant.OrderBy(message => message.Offset.Value).Select(EventIdOf));
        Assert.True(messages.Select(message => message.Partition.Value).Distinct().Count() > 1, "Different tenants never spread over the partitions.");
    }

    [Fact]
    public async Task ADualPublishedFact_ArrivesAsTwoMessagesOfOneFact_OnOnePartition()
    {
        var (database, topic) = await ArrangeAsync();
        await using var _ = database;
        await using var __ = topic;
        var eventId = Guid.CreateVersion7();
        var tenant = Guid.CreateVersion7().ToString("D");
        await OutboxRows.InsertAsync(database, eventId, 1, tenant, topic.Name, publishedVersions: [1, 2]);
        await OutboxRows.InsertAsync(database, eventId, 2, tenant, topic.Name, publishedVersions: [1, 2]);

        await using var relay = KafkaRelay(database, topic.Name);
        await DrainAsync(relay, database);

        var messages = topic.ReadAtLeast(2, Deadline);
        Assert.Single(messages.Select(message => message.Partition.Value).Distinct());
        Assert.All(messages, message => Assert.Equal(eventId, EventIdOf(message)));
        Assert.Equal(["1", "2"], messages.OrderBy(message => message.Offset.Value).Select(message => Header(message, "event-version")));
        Assert.All(messages, message =>
        {
            using var envelope = JsonDocument.Parse(message.Message.Value);
            Assert.Equal(eventId.ToString("D"), envelope.RootElement.GetProperty("eventId").GetString());
            Assert.Equal(Header(message, "event-version"), envelope.RootElement.GetProperty("eventVersion").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal([1, 2], envelope.RootElement.GetProperty("publishedVersions").EnumerateArray().Select(version => version.GetInt32()));
        });
    }

    [Fact]
    public async Task ACrashAfterTheBrokerAcknowledged_BeforeMarking_PublishesTheSameEventIdAgain_WithIdenticalBytes()
    {
        var (database, topic) = await ArrangeAsync();
        await using var _ = database;
        await using var __ = topic;
        var eventId = Guid.CreateVersion7();
        await OutboxRows.InsertAsync(database, eventId, 1, topic: topic.Name);

        await using (var crashing = KafkaRelay(database, topic.Name, wrapStore: inner => new CrashBeforeMarkingStore(inner)))
        {
            // A new topic's metadata may take a moment: the relay backs off until it confirms the topic.
            await Eventually.TrueAsync(
                async () =>
                {
                    await crashing.CycleAsync();
                    return ((CrashBeforeMarkingStore)crashing.Store).CrashedAfterDelivery;
                },
                Deadline,
                "a delivery acknowledged before the simulated crash");
        }

        Assert.Null(Assert.Single(await OutboxRows.ReadAsync(database)).PublishedAt);
        await using (var restarted = KafkaRelay(database, topic.Name))
        {
            await DrainAsync(restarted, database);
        }

        var messages = topic.ReadAtLeast(2, Deadline);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.Equal(eventId, EventIdOf(message)));
        Assert.Equal(messages[0].Message.Value, messages[1].Message.Value);
        Assert.Equal(messages[0].Message.Key, messages[1].Message.Key);
        Assert.NotNull(Assert.Single(await OutboxRows.ReadAsync(database)).PublishedAt);
    }

    [Fact]
    public async Task AStoppedRelayWithPendingRows_IsDrainedByTheNextOne()
    {
        var (database, topic) = await ArrangeAsync();
        await using var _ = database;
        await using var __ = topic;
        var eventIds = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            eventIds.Add(Guid.CreateVersion7());
            await OutboxRows.InsertAsync(database, eventIds[^1], 1, topic: topic.Name);
        }

        await using (var first = KafkaRelay(database, topic.Name, batchSize: 3))
        {
            // One batch, then the relay stops (a new topic's metadata may take a moment to confirm).
            await Eventually.TrueAsync(
                async () =>
                {
                    await first.CycleAsync();
                    return (await OutboxRows.ReadAsync(database)).Any(row => row.PublishedAt is not null);
                },
                Deadline,
                "the first batch published");
        }

        Assert.Equal(7, (await OutboxRows.ReadAsync(database)).Count(row => row.PublishedAt is null));
        await using (var next = KafkaRelay(database, topic.Name, batchSize: 3))
        {
            await DrainAsync(next, database);
        }

        var messages = topic.ReadAtLeast(10, Deadline);
        Assert.Equal(eventIds.Order(), messages.Select(EventIdOf).Distinct().Order());
    }

    [Fact]
    public async Task AGracefulShutdown_MarksOnlyWhatTheBrokerAcknowledged_AndTheRestIsPublishedLater()
    {
        var (database, topic) = await ArrangeAsync();
        await using var _ = database;
        await using var __ = topic;
        var eventIds = new HashSet<Guid>();
        for (var i = 0; i < 60; i++)
        {
            var eventId = Guid.CreateVersion7();
            eventIds.Add(eventId);
            await OutboxRows.InsertAsync(database, eventId, 1, topic: topic.Name);
        }

        await using (var hosted = KafkaRelay(database, topic.Name, batchSize: 5))
        {
            await hosted.Relay.StartAsync(TestContext.Current.CancellationToken);
            await Eventually.TrueAsync(async () => (await OutboxRows.ReadAsync(database)).Any(row => row.PublishedAt is not null), Deadline, "a first batch published");
            await hosted.Relay.StopAsync(TestContext.Current.CancellationToken);
        }

        // Every row marked published reached the broker: marking follows the acknowledgement.
        var marked = (await OutboxRows.ReadAsync(database)).Where(row => row.PublishedAt is not null).Select(row => row.EventId).ToHashSet();
        var delivered = topic.ReadUntil(messages => marked.IsSubsetOf(messages.Select(EventIdOf)), Deadline);
        Assert.Subset(delivered.Select(EventIdOf).ToHashSet(), marked);

        await using (var next = KafkaRelay(database, topic.Name, batchSize: 5))
        {
            await DrainAsync(next, database);
        }

        var all = topic.ReadUntil(messages => eventIds.IsSubsetOf(messages.Select(EventIdOf)), Deadline);
        Assert.Equal(eventIds, all.Select(EventIdOf).ToHashSet());
    }

    [Fact]
    public async Task AMissingTopic_IsReportedByLogAndHealth_NeverCreated_AndTheEventStaysPending()
    {
        KafkaTestEnvironment.RequireAvailable();
        fixture.RequireAvailable();
        var database = new SqlServerTestDatabase();
        await database.InitializeAsync();
        await using var _ = database;
        var missing = $"test.missing.{Guid.NewGuid():N}";
        await using var factory = KafkaHost(database, missing);

        var tenantId = await CreateAsync(factory, "/api/v1/tenants", fixture.UserToken("user-kafka-missing"), new { slug = "kafka-missing-topic", displayName = "Missing Topic" }, "corr-missing");

        using var client = WebApplicationFactoryClient(factory);
        await Eventually.TrueAsync(
            async () => await (await client.GetAsync("/health/outbox", TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken) == "Degraded",
            Deadline,
            "the outbox check reports Degraded");
        Assert.Contains(fixture.Logs.AllText(), text => text.Contains(missing, StringComparison.Ordinal) && text.Contains("Missing", StringComparison.Ordinal));
        Assert.False(KafkaTestEnvironment.TopicExists(missing));
        var row = Assert.Single(await OutboxRows.TenantCreatedAsync(database, tenantId));
        Assert.Null(row.PublishedAt);
    }

    internal static async Task<(SqlServerTestDatabase Database, KafkaTestTopic Topic)> ArrangeAsync()
    {
        KafkaTestEnvironment.RequireAvailable();
        var database = new SqlServerTestDatabase();
        database.RequireAvailable();
        await database.InitializeAsync();
        return (database, await KafkaTestEnvironment.CreateTopicAsync());
    }

    internal WebApplicationFactory<Program> KafkaHost(SqlServerTestDatabase database, string topic, Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? configure = null) =>
        fixture.CreateFactory(database.ConnectionString, builder =>
        {
            builder.UseSetting("TenantMerchant:Kafka:BootstrapServers", KafkaTestEnvironment.BootstrapServers);
            builder.UseSetting("TenantMerchant:Outbox:Topic", topic);
            builder.UseSetting("TenantMerchant:Outbox:PollInterval", "00:00:00.100");
            configure?.Invoke(builder);
        });

    internal static async Task<Guid> CreateAsync(WebApplicationFactory<Program> factory, string path, string token, object body, string correlationId)
    {
        using var client = TenantMerchantApiFixture.CreateClient(factory, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-Correlation-ID", correlationId);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return created.RootElement.GetProperty("id").GetGuid();
    }

    internal static HttpClient WebApplicationFactoryClient(WebApplicationFactory<Program> factory) => TenantMerchantApiFixture.CreateClient(factory, null);

    internal static RelayHarness KafkaRelay(SqlServerTestDatabase database, string topic, int batchSize = 100, Func<IOutboxStore, IOutboxStore>? wrapStore = null) =>
        new(
            database.ConnectionString,
            new OutboxOptions { Topic = topic, BatchSize = batchSize, PollInterval = TimeSpan.FromMilliseconds(100) },
            new KafkaOutboxPublisher(Options.Create(new KafkaOptions { BootstrapServers = KafkaTestEnvironment.BootstrapServers, MessageTimeout = TimeSpan.FromSeconds(10) }), NullLogger<KafkaOutboxPublisher>.Instance),
            wrapStore);

    /// <summary>Runs relay cycles until no row is pending, within the deadline. A cycle's own wait is not slept.</summary>
    internal static async Task DrainAsync(RelayHarness relay, SqlServerTestDatabase database) =>
        await Eventually.TrueAsync(
            async () =>
            {
                await relay.CycleAsync();
                return (await OutboxRows.ReadAsync(database)).All(row => row.PublishedAt is not null);
            },
            Deadline,
            "every row published");

    internal static Guid EventIdOf(ConsumeResult<string, byte[]> message) => Guid.Parse(Header(message, "event-id"));

    internal static string Header(ConsumeResult<string, byte[]> message, string name) =>
        Encoding.UTF8.GetString(message.Message.Headers.GetLastBytes(name));

    private static IEnumerable<(string, string)> Headers(ConsumeResult<string, byte[]> message) =>
        message.Message.Headers.Select(header => (header.Key, Encoding.UTF8.GetString(header.GetValueBytes())));
}
