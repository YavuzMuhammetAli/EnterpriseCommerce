using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;
using EnterpriseCommerce.TenantMerchant.UnitTests.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>
/// The relay's decisions against fake store, producer and leadership seams (PHASE_3_S4_EVENT_FLOW.md section 8):
/// sequential publication, prefix marking, the head-of-line halt, backoff, standby, shutdown and cleanup.
/// </summary>
public sealed class OutboxRelayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeOutboxStore _store = new();
    private readonly FakeOutboxPublisher _publisher = new();
    private readonly FakeOutboxLeadership _leadership = new();
    private readonly OutboxRelayState _state = new();
    private readonly FixedTimeProvider _time = new(Now);
    private readonly OutboxOptions _settings = new() { BatchSize = 3, HaltAfterAttempts = 4 };

    [Fact]
    public async Task AStandby_PublishesNothing_AndTriesTheLockAgainAfterFivePollIntervals()
    {
        _leadership.Granted = false;
        _store.Add();

        var delay = await Relay().RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(_settings.PollInterval * 5, delay);
        Assert.Empty(_publisher.Published);
        Assert.Equal(0, _publisher.TopicChecks);
        Assert.False(_state.Current.IsLeader);
        Assert.Equal(1, _state.Current.Backlog.Count);
    }

    [Fact]
    public async Task TheLeader_PublishesInSequenceOrder_AndMarksTheDeliveredPrefixInOneStatement()
    {
        _store.Add();
        _store.Add();

        var delay = await Relay().RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L], _publisher.Published);
        Assert.Equal([1L, 2L], Assert.Single(_store.MarkCalls));
        Assert.All(_store.Rows, row => Assert.Equal(Now, row.PublishedAt));
        Assert.Equal(_settings.PollInterval, delay);
        Assert.True(_state.Current.IsLeader);
        Assert.Equal(0, _state.Current.Backlog.Count);
    }

    [Fact]
    public async Task AFullBatch_StartsTheNextOneAtOnce()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Add();
        }

        var relay = Relay();

        Assert.Equal(TimeSpan.Zero, await relay.RunCycleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(_settings.PollInterval, await relay.RunCycleAsync(TestContext.Current.CancellationToken));
        Assert.Equal([1L, 2L, 3L, 4L, 5L], _publisher.Published);
    }

    [Fact]
    public async Task AFailure_StopsTheBatch_MarksOnlyThePrefix_AndRecordsTheFailureOnTheFailedRow()
    {
        _store.Add();
        _store.Add();
        _store.Add();
        _publisher.Respond = message => message.Sequence == 2 ? PublishResult.Failure(PublishFailureCategory.Transient, "Local_MsgTimedOut") : PublishResult.Success;

        var delay = await Relay(jitter: 1).RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1L], _publisher.Published);
        Assert.Equal([1L], Assert.Single(_store.MarkCalls));
        Assert.Equal((2L, "Transient:Local_MsgTimedOut"), Assert.Single(_store.Failures));
        Assert.Equal((1, Now), (_store.Rows[1].Attempts, _store.Rows[1].LastAttemptAt!.Value));
        Assert.Null(_store.Rows[2].PublishedAt);
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.Equal(PublishFailureCategory.Transient, _state.Current.LastFailure);
        Assert.False(_state.Current.IsHalted);
        Assert.Equal(Now + delay, _state.Current.BackingOffUntil);
    }

    [Fact]
    public async Task AFailingHeadRow_IsNeverSkipped_AndNothingAfterItIsSent()
    {
        _store.Add();
        _store.Add();
        _publisher.Respond = message => message.Sequence == 1 ? PublishResult.Failure(PublishFailureCategory.Transient, "Local_Transport") : PublishResult.Success;
        var relay = Relay(jitter: 1);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var delay = await relay.RunCycleAsync(TestContext.Current.CancellationToken);
            _time.Now += delay;
        }

        Assert.Empty(_publisher.Published);
        Assert.Equal(3, _store.Rows[0].Attempts);
        Assert.All(_store.Rows, row => Assert.Null(row.PublishedAt));
    }

    [Fact]
    public async Task WhileTheHeadRowWaitsOutItsBackoff_NothingIsPublished()
    {
        _store.Add();
        _publisher.Respond = _ => PublishResult.Failure(PublishFailureCategory.Transient, "Local_Transport");
        var relay = Relay(jitter: 1);
        var backoff = await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        _publisher.Respond = _ => PublishResult.Success;

        _time.Now += backoff / 2;
        var remaining = await relay.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(backoff / 2, remaining);
        Assert.Empty(_publisher.Published);

        _time.Now += remaining;
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L], _publisher.Published);
    }

    [Fact]
    public async Task AfterARestart_TheStoredAttemptsKeepTheBackoff()
    {
        // The row failed three times before the restart, the last time one second ago: attempt 4 is due after 4 s.
        _store.Add(attempts: 3, lastAttemptAt: Now - TimeSpan.FromSeconds(1));

        var delay = await Relay(jitter: 1).RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(3), delay);
        Assert.Empty(_publisher.Published);
    }

    [Theory]
    [InlineData(1, 1.0, 1)]
    [InlineData(2, 1.0, 2)]
    [InlineData(3, 1.0, 4)]
    [InlineData(6, 1.0, 32)]
    [InlineData(7, 1.0, 60)]
    [InlineData(1000, 1.0, 60)]
    [InlineData(1, 0.0, 0.5)]
    [InlineData(7, 0.0, 30)]
    [InlineData(3, 0.5, 3)]
    public void TheBackoff_DoublesFromOneSecond_IsCappedAtTheMaximum_AndJittersBetweenHalfAndAll(int attempts, double jitter, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxBackoff.For(attempts, TimeSpan.FromSeconds(60), jitter));
    }

    [Theory]
    [InlineData("Topic")]
    [InlineData("Authorization")]
    [InlineData("Rejected")]
    [InlineData("Configuration")]
    public async Task APermanentFailure_HaltsTheRelayAtOnce_WithoutSkippingTheRow(string permanentCategory)
    {
        var category = Enum.Parse<PublishFailureCategory>(permanentCategory);
        _store.Add();
        _store.Add();
        _publisher.Respond = message => message.Sequence == 1 ? PublishResult.Failure(category, "Code") : PublishResult.Success;

        await Relay().RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(_state.Current.IsHalted);
        Assert.Equal(category, _state.Current.LastFailure);
        Assert.Empty(_publisher.Published);
        Assert.All(_store.Rows, row => Assert.Null(row.PublishedAt));
    }

    [Fact]
    public async Task TransientFailures_HaltTheRelayAfterTheConfiguredAttempts_AndASuccessResumesIt()
    {
        _store.Add(attempts: 2, lastAttemptAt: Now - TimeSpan.FromHours(1));
        _publisher.Respond = _ => PublishResult.Failure(PublishFailureCategory.Transient, "Local_AllBrokersDown");
        var relay = Relay(jitter: 1);

        var delay = await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.False(_state.Current.IsHalted);

        _time.Now += delay;
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.True(_state.Current.IsHalted);
        Assert.Equal(4, _store.Rows[0].Attempts);

        _publisher.Respond = _ => PublishResult.Success;
        _time.Now += TimeSpan.FromMinutes(5);
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.False(_state.Current.IsHalted);
        Assert.Null(_state.Current.LastFailure);
        Assert.Equal([1L], _publisher.Published);
    }

    [Fact]
    public async Task OnShutdown_TheDeliveryInFlightCompletes_NoNewRowIsTaken_AndTheDeliveredPrefixIsMarked()
    {
        _store.Add();
        _store.Add();
        _store.Add();
        using var stopping = new CancellationTokenSource();
        _publisher.DuringDelivery = message =>
        {
            if (message.Sequence == 2)
            {
                stopping.Cancel();
            }
        };

        await Relay().RunCycleAsync(stopping.Token);

        Assert.Equal([1L, 2L], _publisher.Published);
        Assert.Equal([1L, 2L], Assert.Single(_store.MarkCalls));
        Assert.Null(_store.Rows[2].PublishedAt);
    }

    [Theory]
    [InlineData("Missing")]
    [InlineData("Unauthorized")]
    [InlineData("Unreachable")]
    public async Task WithoutItsTopic_TheRelayBacksOff_PublishesNothing_AndReportsIt(string topicStatus)
    {
        var topic = Enum.Parse<TopicStatus>(topicStatus);
        _store.Add();
        _publisher.Topic = topic;
        var relay = Relay();

        var delay = await relay.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.True(delay > TimeSpan.Zero);
        Assert.Empty(_publisher.Published);
        Assert.Equal(topic, _state.Current.Topic);

        _publisher.Topic = TopicStatus.Exists;
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L], _publisher.Published);
        Assert.Equal(2, _publisher.TopicChecks);
    }

    [Fact]
    public async Task ATopicFailureWhilePublishing_ConfirmsTheTopicAgainBeforeTheNextAttempt()
    {
        _store.Add();
        _publisher.Respond = _ => PublishResult.Failure(PublishFailureCategory.Topic, "UnknownTopicOrPart");
        var relay = Relay();
        _time.Now += await relay.RunCycleAsync(TestContext.Current.CancellationToken);

        await relay.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _publisher.TopicChecks);
    }

    [Fact]
    public async Task ADatabaseFailure_BacksOff_AndTheNextCycleRecovers()
    {
        _store.Add();
        _store.FailNextRead = new TimeoutException("database");
        var relay = Relay(jitter: 1);

        var delay = await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(1), delay);
        Assert.Empty(_publisher.Published);

        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal([1L], _publisher.Published);
    }

    [Fact]
    public async Task TheCleanup_RunsOncePerInterval_InBatches_WithTheRetentionCutoff()
    {
        _store.CleanupResults.Enqueue(1000);
        _store.CleanupResults.Enqueue(7);
        var relay = Relay();

        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal([Now - _settings.PublishedRetention, Now - _settings.PublishedRetention], _store.CleanupCutoffs);

        _time.Now += _settings.CleanupInterval;
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, _store.CleanupCutoffs.Count);
    }

    [Fact]
    public async Task ANewLeader_ChecksTheTopicFirst_AndAStandbyNever()
    {
        var relay = Relay();
        _leadership.Granted = false;
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, _publisher.TopicChecks);

        _leadership.Granted = true;
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        await relay.RunCycleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, _publisher.TopicChecks);
        Assert.True(_state.Current.IsLeader);
    }

    [Fact]
    public async Task WithoutABroker_TheRelayDoesNotRun()
    {
        _store.Add();
        using var relay = Relay(kafka: new KafkaOptions());

        await relay.StartAsync(TestContext.Current.CancellationToken);
        await relay.ExecuteTask!;

        Assert.False(_state.Current.Enabled);
        Assert.Empty(_publisher.Published);
    }

    private OutboxRelay Relay(double jitter = 1, KafkaOptions? kafka = null) => new(
        _store,
        _publisher,
        _leadership,
        _state,
        Options.Create(_settings),
        Options.Create(kafka ?? new KafkaOptions { BootstrapServers = "localhost:9094" }),
        _time,
        NullLogger<OutboxRelay>.Instance,
        () => jitter);
}
