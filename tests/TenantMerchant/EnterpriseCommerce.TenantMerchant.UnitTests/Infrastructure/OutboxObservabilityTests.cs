using System.Diagnostics.Metrics;
using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;
using EnterpriseCommerce.TenantMerchant.UnitTests.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>
/// The <c>outbox</c> health check and the relay's meters (ADR-023 section 6, PHASE_3_S4_EVENT_FLOW.md section 7). The
/// meter is process-wide, so these tests run alone, after the parallel ones.
/// </summary>
[Collection(nameof(OutboxMetricsCollection))]
public sealed class OutboxObservabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeOutboxStore _store = new();
    private readonly OutboxRelayState _state = new(OutboxMetrics.For(TenantMerchantOutbox.MeterName));
    private readonly FixedTimeProvider _time = new(Now);

    [Fact]
    public async Task WithNoBacklog_AndAWorkingRelay_TheOutboxIsHealthy()
    {
        _state.Update(status => status with { Enabled = true, IsLeader = true, Topic = TopicStatus.Exists });

        Assert.Equal(HealthStatus.Healthy, await CheckAsync());
    }

    [Fact]
    public async Task APendingRowOlderThanTheThreshold_IsDegraded_OnEveryInstance()
    {
        // The fake store's rows occurred at the Unix epoch: far older than StaleAfter.
        _store.Add();
        _state.Update(status => status with { Enabled = true, IsLeader = false });

        Assert.Equal(HealthStatus.Degraded, await CheckAsync());
    }

    [Fact]
    public async Task ALeaderThatIsHaltedBackingOffOrWithoutItsTopic_IsDegraded()
    {
        _state.Update(status => status with { Enabled = true, IsLeader = true, Topic = TopicStatus.Exists, IsHalted = true });
        Assert.Equal(HealthStatus.Degraded, await CheckAsync());

        _state.Update(status => status with { IsHalted = false, BackingOffUntil = Now.AddSeconds(5) });
        Assert.Equal(HealthStatus.Degraded, await CheckAsync());

        _state.Update(status => status with { BackingOffUntil = Now.AddSeconds(-1), Topic = TopicStatus.Missing });
        Assert.Equal(HealthStatus.Degraded, await CheckAsync());

        _state.Update(status => status with { Topic = TopicStatus.Exists });
        Assert.Equal(HealthStatus.Healthy, await CheckAsync());
    }

    [Fact]
    public async Task AStandby_IsNotDegradedByItsOwnIdleState()
    {
        _state.Update(status => status with { Enabled = true, IsLeader = false, Topic = TopicStatus.Unknown });

        Assert.Equal(HealthStatus.Healthy, await CheckAsync());
    }

    [Fact]
    public async Task TheMeters_CountPublicationsFailuresAndHalts_TaggedByCategoryOnly()
    {
        var measurements = new List<(string Instrument, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TenantMerchantOutbox.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();

        var publisher = new FakeOutboxPublisher();
        var relay = new OutboxRelay(
            _store, publisher, new FakeOutboxLeadership(), _state,
            Options.Create(new OutboxOptions { Topic = TenantMerchantOutbox.DefaultTopic, HaltAfterAttempts = 2 }), Options.Create(new KafkaOptions { BootstrapServers = "localhost:9094" }),
            _time, NullLogger<OutboxRelay>.Instance, () => 1);
        _store.Add();
        _store.Add();
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);

        _store.Add();
        publisher.Respond = _ => PublishResult.Failure(PublishFailureCategory.Rejected, "MsgSizeTooLarge");
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);

        _state.Metrics.Observe(_state);
        listener.RecordObservableInstruments();
        listener.Dispose();

        Assert.Equal(2, measurements.Where(m => m.Instrument == "outbox.published").Sum(m => m.Value));
        var failure = Assert.Single(measurements, m => m.Instrument == "outbox.publish_failures");
        Assert.Equal([new KeyValuePair<string, object?>("category", "rejected")], failure.Tags);
        Assert.Equal("rejected", Assert.Single(measurements, m => m.Instrument == "outbox.halts").Tags.Single().Value);
        Assert.Equal(3, measurements.Count(m => m.Instrument == "outbox.publish.duration"));
        Assert.Equal(2, measurements.Count(m => m.Instrument == "outbox.cycle.duration"));
        Assert.Equal(1, measurements.Single(m => m.Instrument == "outbox.pending").Value);
        Assert.Equal(1, measurements.Single(m => m.Instrument == "outbox.halted").Value);
        Assert.Equal(1, measurements.Single(m => m.Instrument == "outbox.leader").Value);
        Assert.True(measurements.Single(m => m.Instrument == "outbox.oldest_pending_age_seconds").Value > 0);

        // Bounded tags: nothing that identifies a tenant, an event or a request.
        Assert.All(measurements.SelectMany(m => m.Tags), tag => Assert.Equal("category", tag.Key));
    }

    private async Task<HealthStatus> CheckAsync() =>
        (await new OutboxHealthCheck(_store, _state, Options.Create(TenantMerchantOutbox.DefaultOptions()), _time)
            .CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken)).Status;
}

/// <summary>The outbox meter is process-wide: its tests run alone.</summary>
[CollectionDefinition(nameof(OutboxMetricsCollection), DisableParallelization = true)]
public sealed class OutboxMetricsCollection;
