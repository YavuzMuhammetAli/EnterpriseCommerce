using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.Inventory.Infrastructure.Messaging;
using EnterpriseCommerce.Inventory.Infrastructure.Persistence;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Inventory.UnitTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Kafka;

/// <summary>
/// Inventory's events through the shared outbox relay to a real Kafka broker (ADR-028, ADR-023 sections 4 to 6): end to
/// end from the APIs, refused requests publishing nothing, recovery of a backlog by a new host, redelivery with identical
/// bytes, and a missing topic halting the relay without skipping. Every test has its own database and topic.
/// </summary>
public sealed class KafkaPublicationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CommittedChanges_ArePublished_KeyedByTheTenant_InAggregateOrder_WithTheirEnvelopeAndHeaders()
    {
        await using var arranged = await ArrangeAsync();
        var (fixture, topic) = (arranged.Fixture, arranged.Topic);
        await using var host = KafkaHost(fixture, topic.Name);
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var first = await CreateItemAsync(fixture, host, tenantId, owner, 10);
        var second = await CreateItemAsync(fixture, host, tenantId, owner, 5);
        await SendAsync(host, fixture.UserToken(owner), HttpMethod.Post, $"/api/v1/inventory/tenants/{tenantId}/items/{first.GetProperty("id").GetGuid()}/adjustments", new { quantityChange = 3, reason = "Receipt" }, "corr-kafka-adjust", HttpStatusCode.Created);
        var reservation = await SendAsync(host, fixture.ServiceToken(), HttpMethod.Post, $"/internal/v1/inventory/tenants/{tenantId}/reservations", new { lines = new[] { Line(first, 2), Line(second, 1) } }, "corr-kafka-reserve", HttpStatusCode.Created);
        await SendAsync(host, fixture.ServiceToken(), HttpMethod.Post, $"/internal/v1/inventory/tenants/{tenantId}/reservations/{reservation.GetProperty("id").GetGuid()}/commit", null, "corr-kafka-commit", HttpStatusCode.OK);

        // 2 initial stocks, 1 adjustment, 1 + 2 for the reservation, 1 + 2 for the commit.
        var messages = topic.ReadAtLeast(9, Deadline);
        await Eventually.TrueAsync(async () => await PendingAsync(fixture) == 0, Deadline, "every row marked published");

        Assert.Equal(9, messages.Count);
        Assert.All(messages, message => Assert.Equal(tenantId.ToString("D"), message.Message.Key));
        Assert.Single(messages.Select(message => message.Partition.Value).Distinct());
        var rows = await RowsAsync(fixture);
        foreach (var message in messages)
        {
            var body = Encoding.UTF8.GetString(message.Message.Value);
            var row = Assert.Single(rows, candidate => candidate.Envelope == body);
            EventSchemas.AssertValid(body);
            Assert.Equal(
                [("event-id", row.EventId.ToString("D")), ("event-type", row.EventType), ("event-version", "1"), ("correlation-id", CorrelationOf(body)), ("content-type", "application/json")],
                message.Message.Headers.Select(header => (header.Key, Encoding.UTF8.GetString(header.GetValueBytes()))));
        }

        // One aggregate's facts arrive in commit order: each item's versions ascend, the reservation is created first.
        foreach (var item in new[] { first, second })
        {
            var versions = messages.Select(message => JsonDocument.Parse(message.Message.Value).RootElement)
                .Where(envelope => envelope.GetProperty("aggregateId").GetGuid() == item.GetProperty("id").GetGuid())
                .Select(envelope => envelope.GetProperty("payload").GetProperty("itemVersion").GetInt64())
                .ToList();
            Assert.Equal(versions.Order(), versions);
        }

        Assert.Equal(
            ["ReservationCreated", "ReservationCommitted"],
            messages.Select(message => message.Message.Headers.GetLastBytes("event-type")).Select(Encoding.UTF8.GetString).Where(type => type.StartsWith("Reservation", StringComparison.Ordinal)));
        Assert.Contains(messages, message => CorrelationOf(Encoding.UTF8.GetString(message.Message.Value)) == "corr-kafka-commit");
    }

    [Fact]
    public async Task RefusedRequests_WriteNoRowAndPublishNothing()
    {
        await using var arranged = await ArrangeAsync();
        var (fixture, topic) = (arranged.Fixture, arranged.Topic);
        await using var host = KafkaHost(fixture, topic.Name);
        var tenantId = fixture.NewTenant(out var owner, out _, out var staff);
        var item = await CreateItemAsync(fixture, host, tenantId, owner, 1);
        var path = $"/api/v1/inventory/tenants/{tenantId}/items/{item.GetProperty("id").GetGuid()}/adjustments";

        await SendAsync(host, fixture.ServiceToken(), HttpMethod.Post, $"/internal/v1/inventory/tenants/{tenantId}/reservations", new { lines = new[] { Line(item, 2) } }, "corr-refused-1", HttpStatusCode.Conflict);
        await SendAsync(host, fixture.UserToken(owner), HttpMethod.Post, path, new { quantityChange = -2, reason = "Loss" }, "corr-refused-2", HttpStatusCode.Conflict);
        await SendAsync(host, fixture.UserToken(staff), HttpMethod.Post, path, new { quantityChange = 1, reason = "Receipt" }, "corr-refused-3", HttpStatusCode.Forbidden);
        await SendAsync(host, fixture.UserToken(owner), HttpMethod.Post, path, new { quantityChange = 1, reason = "Receipt" }, "corr-accepted", HttpStatusCode.Created);

        var messages = topic.ReadAtLeast(2, Deadline);
        Assert.Equal(2, (await RowsAsync(fixture)).Count);
        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(messages, message => CorrelationOf(Encoding.UTF8.GetString(message.Message.Value)).StartsWith("corr-refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABacklogWrittenWhileKafkaWasAbsent_IsPublishedInOrder_ByTheNextHost()
    {
        await using var arranged = await ArrangeAsync();
        var (fixture, topic) = (arranged.Fixture, arranged.Topic);
        var tenantId = fixture.NewTenant(out var owner, out _, out _);

        // A host without Kafka (Development): the business writes commit, and their rows stay pending.
        JsonElement item;
        await using (var offline = fixture.CreateFactory(builder => builder.UseSetting("Inventory:Outbox:Topic", topic.Name)))
        {
            item = await CreateItemAsync(fixture, offline, tenantId, owner, 10);
            for (var i = 0; i < 5; i++)
            {
                await SendAsync(offline, fixture.UserToken(owner), HttpMethod.Post, $"/api/v1/inventory/tenants/{tenantId}/items/{item.GetProperty("id").GetGuid()}/adjustments", new { quantityChange = 1, reason = "Receipt" }, $"corr-backlog-{i}", HttpStatusCode.Created);
            }
        }

        Assert.Equal(6, await PendingAsync(fixture));

        await using var online = KafkaHost(fixture, topic.Name);
        using (online.CreateClient())
        {
        }

        var messages = topic.ReadAtLeast(6, Deadline);
        await Eventually.TrueAsync(async () => await PendingAsync(fixture) == 0, Deadline, "the backlog is drained");
        Assert.Equal(
            [1L, 2L, 3L, 4L, 5L, 6L],
            messages.Select(message => JsonDocument.Parse(message.Message.Value).RootElement.GetProperty("payload").GetProperty("itemVersion").GetInt64()));
    }

    [Fact]
    public async Task AFactDeliveredBeforeItsMarkingFailed_IsDeliveredAgain_WithTheSameEventIdAndBytes()
    {
        await using var arranged = await ArrangeAsync();
        var (fixture, topic) = (arranged.Fixture, arranged.Topic);
        var tenant = Guid.CreateVersion7();
        await InventorySeed.CreateItemAsync(fixture.Database, tenant, onHand: 3);
        await fixture.Database.ExecuteAsync($"UPDATE OutboxMessages SET Topic = '{topic.Name}'");

        // The first relay delivers and then "crashes" before marking; the next one publishes the row again.
        // A new leader confirms the topic first and may back off while its metadata settles: cycle until done.
        await using (var crashing = new InventoryRelay(fixture.Database.ConnectionString, topic.Name, crashBeforeMarking: true))
        {
            await Eventually.TrueAsync(
                async () =>
                {
                    await crashing.CycleAsync();
                    return crashing.CrashedAfterDelivery;
                },
                Deadline,
                "a delivery acknowledged before the simulated crash");
        }

        Assert.Equal(1, await PendingAsync(fixture));
        await using (var recovering = new InventoryRelay(fixture.Database.ConnectionString, topic.Name, crashBeforeMarking: false))
        {
            await Eventually.TrueAsync(
                async () =>
                {
                    await recovering.CycleAsync();
                    return await PendingAsync(fixture) == 0;
                },
                Deadline,
                "the next relay publishes the row again and marks it");
        }

        var messages = topic.ReadAtLeast(2, Deadline);
        Assert.Equal(2, messages.Count);
        Assert.Equal(messages[0].Message.Value, messages[1].Message.Value);
        Assert.Equal(Encoding.UTF8.GetString(messages[0].Message.Headers.GetLastBytes("event-id")), Encoding.UTF8.GetString(messages[1].Message.Headers.GetLastBytes("event-id")));
        Assert.Equal(0, await PendingAsync(fixture));
    }

    [Fact]
    public async Task AMissingTopic_HaltsTheRelayWithoutSkipping_AndPublicationResumesOnceItExists()
    {
        KafkaTestEnvironment.RequireAvailable();
        await using var fixture = new InventoryApiFixture();
        await fixture.InitializeAsync();
        fixture.RequireAvailable();
        var missing = $"test.inventory.events.{Guid.NewGuid():N}";
        await using var host = KafkaHost(fixture, missing);
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        await CreateItemAsync(fixture, host, tenantId, owner, 4);
        using var client = host.CreateClient();

        await Eventually.TrueAsync(async () => await client.GetStringAsync("/health/outbox", Token) == "Degraded", Deadline, "the outbox reports the missing topic");
        Assert.Equal(1, await PendingAsync(fixture));
        Assert.Equal("Healthy", await client.GetStringAsync("/health/ready", Token));
        Assert.False(KafkaTestEnvironment.TopicExists(missing), "The relay never creates a topic.");

        await using var created = await KafkaTestEnvironment.CreateTopicAsync(name: missing);
        created.ReadAtLeast(1, TimeSpan.FromMinutes(3));
        await Eventually.TrueAsync(async () => await PendingAsync(fixture) == 0, TimeSpan.FromMinutes(3), "the row is published once the topic exists");
    }

    private static async Task<Arranged> ArrangeAsync()
    {
        KafkaTestEnvironment.RequireAvailable();
        var fixture = new InventoryApiFixture();
        await fixture.InitializeAsync();
        fixture.RequireAvailable();
        return new Arranged(fixture, await KafkaTestEnvironment.CreateTopicAsync());
    }

    private static WebApplicationFactory<Program> KafkaHost(InventoryApiFixture fixture, string topic) =>
        fixture.CreateFactory(builder =>
        {
            builder.UseSetting("Inventory:Kafka:BootstrapServers", KafkaTestEnvironment.BootstrapServers);
            builder.UseSetting("Inventory:Kafka:MessageTimeout", "00:00:10");
            builder.UseSetting("Inventory:Outbox:Topic", topic);
            builder.UseSetting("Inventory:Outbox:PollInterval", "00:00:00.100");
            builder.UseSetting("Inventory:Outbox:MaxBackoff", "00:00:02");
        });

    private static async Task<JsonElement> CreateItemAsync(InventoryApiFixture fixture, WebApplicationFactory<Program> host, Guid tenantId, string owner, int onHand)
    {
        var product = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        fixture.Dependencies.SetVariant(tenantId, product, variant, sku: $"SKU-{variant:N}"[..20]);
        return await SendAsync(host, fixture.UserToken(owner), HttpMethod.Post, $"/api/v1/inventory/tenants/{tenantId}/items", new { productId = product, variantId = variant, initialOnHand = onHand }, $"corr-create-{variant:N}"[..40], HttpStatusCode.Created);
    }

    private static async Task<JsonElement> SendAsync(WebApplicationFactory<Program> host, string token, HttpMethod method, string path, object? body, string correlationId, HttpStatusCode expected)
    {
        using var client = InventoryApiFixture.CreateClient(host, token);
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : System.Net.Http.Json.JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        request.Headers.Add("X-Correlation-ID", correlationId);
        using var response = await client.SendAsync(request, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == expected, $"{method} {path}: expected {expected}, got {response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Line(JsonElement item, int quantity) => new { variantId = item.GetProperty("variantId").GetGuid(), quantity };

    private static string CorrelationOf(string envelope) => JsonDocument.Parse(envelope).RootElement.GetProperty("correlationId").GetString()!;

    private static Task<int> PendingAsync(InventoryApiFixture fixture) => fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM OutboxMessages WHERE PublishedAt IS NULL");

    private static async Task<List<OutboxMessage>> RowsAsync(InventoryApiFixture fixture)
    {
        await using var context = fixture.Database.CreateContext();
        return await context.OutboxMessages.AsNoTracking().OrderBy(row => row.Sequence).ToListAsync(Token);
    }

    private sealed record Arranged(InventoryApiFixture Fixture, KafkaTestTopic Topic) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Topic.DisposeAsync();
            await Fixture.DisposeAsync();
        }
    }

    /// <summary>The shared relay with Inventory's profile on a test database, without a web host.</summary>
    private sealed class InventoryRelay : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly CrashBeforeMarkingStore? _crashing;
        private readonly OutboxRelay _relay;
        private readonly SqlOutboxLeadership _leadership;

        public InventoryRelay(string connectionString, string topic, bool crashBeforeMarking)
        {
            var services = new ServiceCollection();
            services.AddDbContext<InventoryDbContext>(options => options.UseSqlServer(connectionString));
            _services = services.BuildServiceProvider();
            IOutboxStore store = new SqlOutboxStore<InventoryDbContext>(_services.GetRequiredService<IServiceScopeFactory>());
            if (crashBeforeMarking)
            {
                store = _crashing = new CrashBeforeMarkingStore(store);
            }

            var kafka = new KafkaOptions { BootstrapServers = KafkaTestEnvironment.BootstrapServers, MessageTimeout = TimeSpan.FromSeconds(10) };
            _leadership = new SqlOutboxLeadership(() => connectionString, InventoryOutbox.Profile.LockApplicationName);
            _relay = new OutboxRelay(
                store,
                new KafkaOutboxPublisher(Options.Create(kafka), InventoryOutbox.Profile, NullLogger<KafkaOutboxPublisher>.Instance),
                _leadership,
                new OutboxRelayState(OutboxMetrics.For(InventoryOutbox.MeterName)),
                Options.Create(new OutboxOptions { Topic = topic, PollInterval = TimeSpan.FromMilliseconds(100) }),
                Options.Create(kafka),
                TimeProvider.System,
                NullLogger<OutboxRelay>.Instance);
        }

        public bool CrashedAfterDelivery => _crashing?.CrashedAfterDelivery == true;

        public Task CycleAsync() => _relay.RunCycleAsync(TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            _relay.Dispose();
            await _leadership.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    /// <summary>An outbox store whose marking fails, as if the process died after the broker acknowledged the batch.</summary>
    private sealed class CrashBeforeMarkingStore(IOutboxStore inner) : IOutboxStore
    {
        public bool CrashedAfterDelivery { get; private set; }

        public Task<IReadOnlyList<PendingOutboxMessage>> ReadPendingAsync(int batchSize, CancellationToken cancellationToken) => inner.ReadPendingAsync(batchSize, cancellationToken);

        public Task MarkPublishedAsync(IReadOnlyList<long> sequences, DateTimeOffset publishedAt, CancellationToken cancellationToken)
        {
            CrashedAfterDelivery = true;
            throw new InvalidOperationException("Simulated crash after delivery, before marking.");
        }

        public Task RecordFailureAsync(long sequence, DateTimeOffset attemptedAt, string error, CancellationToken cancellationToken) => inner.RecordFailureAsync(sequence, attemptedAt, error, cancellationToken);

        public Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken) => inner.GetBacklogAsync(cancellationToken);

        public Task<int> DeletePublishedBeforeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken) => inner.DeletePublishedBeforeAsync(cutoff, batchSize, cancellationToken);
    }
}
