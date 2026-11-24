using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

/// <summary>A publisher that records every delivery instead of calling Kafka, for the relay's SQL Server behaviour.</summary>
internal sealed class RecordingPublisher : IOutboxPublisher
{
    private readonly Lock _gate = new();
    private readonly List<PendingOutboxMessage> _deliveries = [];

    public IReadOnlyList<PendingOutboxMessage> Deliveries
    {
        get
        {
            lock (_gate)
            {
                return [.. _deliveries];
            }
        }
    }

    public Task<PublishResult> PublishAsync(PendingOutboxMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _deliveries.Add(message);
        }

        return Task.FromResult(PublishResult.Success);
    }

    public Task<TopicStatus> CheckTopicAsync(string topic, CancellationToken cancellationToken) => Task.FromResult(TopicStatus.Exists);

    public void Dispose()
    {
    }
}

/// <summary>An outbox store whose marking fails, as if the process died after the broker acknowledged the batch.</summary>
internal sealed class CrashBeforeMarkingStore(IOutboxStore inner) : IOutboxStore
{
    /// <summary>Set when the relay tried to mark, which it does only after the broker acknowledged a delivery.</summary>
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

/// <summary>
/// The relay's real SQL Server components (store and leadership) on a test database, without a web host: one instance
/// per simulated service process.
/// </summary>
internal sealed class RelayHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;

    public RelayHarness(string connectionString, OutboxOptions? settings = null, IOutboxPublisher? publisher = null, Func<IOutboxStore, IOutboxStore>? wrapStore = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TenantMerchantDbContext>(options => options.UseSqlServer(connectionString));
        _services = services.BuildServiceProvider();

        Settings = settings ?? new OutboxOptions { Topic = TenantMerchantOutbox.DefaultTopic, PollInterval = TimeSpan.FromMilliseconds(100) };
        var store = new SqlOutboxStore<TenantMerchantDbContext>(_services.GetRequiredService<IServiceScopeFactory>());
        Store = wrapStore is null ? store : wrapStore(store);
        Leadership = new SqlOutboxLeadership(() => connectionString, TenantMerchantOutbox.Profile.LockApplicationName);
        Publisher = publisher ?? new RecordingPublisher();
        Relay = new OutboxRelay(
            Store,
            Publisher,
            Leadership,
            State,
            Options.Create(Settings),
            Options.Create(new KafkaOptions { BootstrapServers = "unused:9092" }),
            TimeProvider.System,
            NullLogger<OutboxRelay>.Instance);
    }

    public OutboxOptions Settings { get; }

    public IOutboxStore Store { get; }

    public SqlOutboxLeadership Leadership { get; }

    public IOutboxPublisher Publisher { get; }

    public OutboxRelayState State { get; } = new(OutboxMetrics.For(TenantMerchantOutbox.MeterName));

    public OutboxRelay Relay { get; }

    public IReadOnlyList<PendingOutboxMessage> Deliveries => ((RecordingPublisher)Publisher).Deliveries;

    public Task<TimeSpan> CycleAsync() => Relay.RunCycleAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        Relay.Dispose();
        await Leadership.DisposeAsync();
        await _services.DisposeAsync();
    }
}
