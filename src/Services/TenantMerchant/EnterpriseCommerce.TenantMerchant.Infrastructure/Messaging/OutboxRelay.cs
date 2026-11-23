using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

/// <summary>
/// Publishes committed outbox rows to Kafka (ADR-023 sections 5 and 6, PHASE_3_S4_EVENT_FLOW.md section 5). A hosted
/// service, never part of a request: the API returns after the business commit and never waits for Kafka.
/// </summary>
/// <remarks>
/// <para>
/// One instance per service publishes: the leader of <see cref="IOutboxLeadership"/>; the others stand by. The leader
/// reads committed rows in <c>Sequence</c> order and publishes them one at a time, awaiting each delivery report before
/// sending the next, so a tenant's messages reach their partition in commit order. The delivered prefix of a batch is
/// marked <c>PublishedAt</c> in one statement after the batch; no transaction is open while Kafka is called.
/// </para>
/// <para>
/// Delivery is at least once: a crash or a leadership change between a delivery and its marking publishes the row
/// again, with the same bytes and event ID. A row that fails is retried with exponential backoff and jitter, and nothing
/// after it is sent: a committed fact is never skipped (ADR-023 section 6). A permanent failure, or
/// <see cref="OutboxOptions.HaltAfterAttempts"/> consecutive failures, puts the relay in the halted state, which is
/// logged, counted and reported by the <c>outbox</c> health check; the relay keeps retrying at the capped backoff and
/// resumes by itself once an operator fixes the cause. Attempts are stored on the row, so a restart keeps the backoff.
/// </para>
/// </remarks>
internal sealed partial class OutboxRelay(
    IOutboxStore store,
    IOutboxPublisher publisher,
    IOutboxLeadership leadership,
    OutboxRelayState state,
    IOptions<OutboxOptions> outboxOptions,
    IOptions<KafkaOptions> kafkaOptions,
    TimeProvider timeProvider,
    ILogger<OutboxRelay> logger,
    Func<double>? jitter = null) : BackgroundService
{
    /// <summary>The longest a marking or failure record may take during shutdown.</summary>
    private static readonly TimeSpan ShutdownWriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Published rows deleted per statement by the cleanup.</summary>
    private const int CleanupBatchSize = 1000;

    private readonly OutboxOptions _settings = outboxOptions.Value;
    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    private bool _isLeader;
    private bool _topicReady;
    private int _consecutiveCycleFailures;
    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;
    private RetryGate? _retryGate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!kafkaOptions.Value.IsConfigured)
        {
            // Only possible in Development: elsewhere the options validation refuses to start (ADR-023 section 6).
            LogRelayDisabled(logger);
            return;
        }

        state.Update(status => status with { Enabled = true });
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = await RunCycleAsync(stoppingToken);
                if (delay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(delay, timeProvider, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            await StopPublishingAsync();
        }
    }

    /// <summary>
    /// One cycle: keep or acquire the leadership, confirm the topic, publish one batch, and clean up when due. Returns
    /// how long to wait before the next cycle: zero while a full batch suggests more rows.
    /// </summary>
    internal async Task<TimeSpan> RunCycleAsync(CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var delay = await RunLeaderCycleAsync(stoppingToken);
            _consecutiveCycleFailures = 0;
            return delay;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return TimeSpan.Zero;
        }
        catch (Exception exception)
        {
            // The database, or anything unexpected: back off and try again; leadership is re-checked next cycle.
            _consecutiveCycleFailures++;
            var backoff = OutboxBackoff.For(_consecutiveCycleFailures, _settings.MaxBackoff, _jitter());
            LogCycleFailed(logger, exception.GetType().Name, _consecutiveCycleFailures, backoff.TotalSeconds);
            state.Update(status => status with { BackingOffUntil = timeProvider.GetUtcNow() + backoff });
            return backoff;
        }
        finally
        {
            OutboxMetrics.CycleDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private async Task<TimeSpan> RunLeaderCycleAsync(CancellationToken stoppingToken)
    {
        if (!await EnsureLeadershipAsync(stoppingToken))
        {
            // A standby: it publishes nothing, and retries the lock every five poll intervals.
            await RefreshBacklogAsync(stoppingToken);
            return _settings.PollInterval * 5;
        }

        if (!_topicReady && await CheckTopicAsync(stoppingToken) is { } topicBackoff)
        {
            return topicBackoff;
        }

        await CleanUpIfDueAsync(stoppingToken);

        var batch = await store.ReadPendingAsync(_settings.BatchSize, stoppingToken);
        if (batch.Count == 0)
        {
            state.Update(status => status with { BackingOffUntil = null, IsHalted = false, LastFailure = null, Backlog = new OutboxBacklog(0, null) });
            return _settings.PollInterval;
        }

        // A head row that failed before, in this process or before a restart, waits out its backoff first.
        if (WaitBeforeRetry(batch[0]) is { } wait)
        {
            await RefreshBacklogAsync(stoppingToken);
            return wait;
        }

        var (delivered, failure) = await PublishBatchAsync(batch, stoppingToken);
        if (delivered.Count > 0)
        {
            await MarkPublishedAsync(delivered, stoppingToken);
        }

        if (failure is { } failed)
        {
            var backoff = await RecordFailureAsync(failed.Message, failed.Result, stoppingToken);
            await RefreshBacklogAsync(stoppingToken);
            return backoff;
        }

        _retryGate = null;
        state.Update(status => status with { BackingOffUntil = null, IsHalted = false, LastFailure = null });
        await RefreshBacklogAsync(stoppingToken);
        return batch.Count == _settings.BatchSize && !stoppingToken.IsCancellationRequested ? TimeSpan.Zero : _settings.PollInterval;
    }

    private async Task<bool> EnsureLeadershipAsync(CancellationToken stoppingToken)
    {
        bool isLeader;
        try
        {
            isLeader = await leadership.TryEnsureAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            isLeader = false;
            LogLeadershipCheckFailed(logger, exception.GetType().Name);
        }

        if (isLeader != _isLeader)
        {
            _isLeader = isLeader;
            if (isLeader)
            {
                LogLeadershipAcquired(logger);

                // A new leader confirms the topic first, and starts from the rows' stored state.
                _topicReady = false;
                _retryGate = null;
            }
            else
            {
                LogLeadershipLost(logger);
            }

            state.Update(status => status with { IsLeader = isLeader });
        }

        return isLeader;
    }

    private async Task<TimeSpan?> CheckTopicAsync(CancellationToken stoppingToken)
    {
        var topic = await publisher.CheckTopicAsync(_settings.Topic, stoppingToken);
        state.Update(status => status with { Topic = topic });
        if (topic == TopicStatus.Exists)
        {
            _topicReady = true;
            return null;
        }

        // Never created and never skipped: the relay waits for an operator, or for the broker (ADR-023 section 2).
        _consecutiveCycleFailures++;
        var backoff = OutboxBackoff.For(_consecutiveCycleFailures, _settings.MaxBackoff, _jitter());
        LogTopicUnavailable(logger, _settings.Topic, topic.ToString(), backoff.TotalSeconds);
        state.Update(status => status with { BackingOffUntil = timeProvider.GetUtcNow() + backoff });
        await RefreshBacklogAsync(stoppingToken);
        return backoff;
    }

    /// <summary>
    /// Publishes the batch in order, awaiting each delivery report. It stops at the first failure, and takes no further
    /// row once the host is stopping; the delivery in flight is always awaited, bounded by <c>message.timeout.ms</c>.
    /// </summary>
    private async Task<(List<long> Delivered, (PendingOutboxMessage Message, PublishResult Result)? Failure)> PublishBatchAsync(
        IReadOnlyList<PendingOutboxMessage> batch,
        CancellationToken stoppingToken)
    {
        var delivered = new List<long>(batch.Count);
        foreach (var message in batch)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var started = Stopwatch.GetTimestamp();
            var result = await publisher.PublishAsync(message, CancellationToken.None);
            OutboxMetrics.PublishDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
            if (!result.Delivered)
            {
                return (delivered, (message, result));
            }

            delivered.Add(message.Sequence);
            OutboxMetrics.Published.Add(1);
        }

        return (delivered, null);
    }

    private async Task MarkPublishedAsync(List<long> delivered, CancellationToken stoppingToken)
    {
        // Persisted only after the broker acknowledged every row of the prefix. During shutdown the marking still runs,
        // briefly bounded, so the next leader does not publish them again; if it fails, it does (at least once).
        using var timeout = stoppingToken.IsCancellationRequested ? new CancellationTokenSource(ShutdownWriteTimeout) : null;
        await store.MarkPublishedAsync(delivered, timeProvider.GetUtcNow(), timeout?.Token ?? stoppingToken);
        LogBatchPublished(logger, delivered.Count);
    }

    private async Task<TimeSpan> RecordFailureAsync(PendingOutboxMessage message, PublishResult result, CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        using var timeout = stoppingToken.IsCancellationRequested ? new CancellationTokenSource(ShutdownWriteTimeout) : null;
        await store.RecordFailureAsync(message.Sequence, now, result.Describe(), timeout?.Token ?? stoppingToken);

        var attempts = message.Attempts + 1;
        OutboxMetrics.PublishFailures.Add(1, OutboxMetrics.CategoryTag(result.Category));
        if (result.Category == PublishFailureCategory.Topic)
        {
            // Confirm the topic again before the next attempt.
            _topicReady = false;
        }

        var backoff = OutboxBackoff.For(attempts, _settings.MaxBackoff, _jitter());
        _retryGate = new RetryGate(message.Sequence, attempts, now + backoff);

        var halted = result.IsPermanent || attempts >= _settings.HaltAfterAttempts;
        var wasHalted = state.Current.IsHalted;
        state.Update(status => status with { BackingOffUntil = now + backoff, IsHalted = halted, LastFailure = result.Category });

        var correlationId = SafeCorrelationId(message.Envelope);
        if (halted && !wasHalted)
        {
            OutboxMetrics.Halts.Add(1, OutboxMetrics.CategoryTag(result.Category));
            LogHalted(logger, message.EventType, message.EventId, message.EventVersion, message.MessageKey, correlationId, result.Category.ToString(), result.Code, attempts);
        }
        else if (halted && attempts % 10 == 0)
        {
            LogStillHalted(logger, message.EventType, message.EventId, message.MessageKey, result.Category.ToString(), result.Code, attempts);
        }
        else
        {
            LogPublishFailed(logger, message.EventType, message.EventId, message.EventVersion, message.MessageKey, correlationId, result.Category.ToString(), result.Code, attempts, backoff.TotalSeconds);
        }

        return backoff;
    }

    /// <summary>The remaining wait before the head row may be attempted again, or <c>null</c> if it is due.</summary>
    private TimeSpan? WaitBeforeRetry(PendingOutboxMessage head)
    {
        if (head.Attempts == 0 || head.LastAttemptAt is not { } lastAttempt)
        {
            return null;
        }

        // After a restart the gate is rebuilt from the row: the stored attempts and time keep the backoff.
        if (_retryGate is not { } gate || gate.Sequence != head.Sequence || gate.Attempts != head.Attempts)
        {
            gate = new RetryGate(head.Sequence, head.Attempts, lastAttempt + OutboxBackoff.For(head.Attempts, _settings.MaxBackoff, _jitter()));
            _retryGate = gate;
        }

        var wait = gate.DueAt - timeProvider.GetUtcNow();
        if (wait <= TimeSpan.Zero)
        {
            return null;
        }

        state.Update(status => status with { BackingOffUntil = gate.DueAt, IsHalted = status.IsHalted || head.Attempts >= _settings.HaltAfterAttempts });
        return wait;
    }

    private async Task CleanUpIfDueAsync(CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < _nextCleanupAt)
        {
            return;
        }

        _nextCleanupAt = now + _settings.CleanupInterval;
        var cutoff = now - _settings.PublishedRetention;
        var deleted = 0;
        int removed;
        do
        {
            removed = await store.DeletePublishedBeforeAsync(cutoff, CleanupBatchSize, stoppingToken);
            deleted += removed;
        }
        while (removed == CleanupBatchSize && !stoppingToken.IsCancellationRequested);

        if (deleted > 0)
        {
            LogCleanedUp(logger, deleted, _settings.PublishedRetention.TotalDays);
        }
    }

    private async Task RefreshBacklogAsync(CancellationToken stoppingToken)
    {
        try
        {
            var backlog = await store.GetBacklogAsync(stoppingToken);
            state.Update(status => status with { Backlog = backlog });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Diagnostics only: the health check reads the table itself.
            LogBacklogReadFailed(logger, exception.GetType().Name);
        }
    }

    /// <summary>Graceful shutdown (ADR-023 section 5): the producer is flushed and disposed, then the lock released.</summary>
    private async Task StopPublishingAsync()
    {
        publisher.Dispose();
        await leadership.ReleaseAsync();
        if (_isLeader)
        {
            _isLeader = false;
            LogLeadershipReleased(logger);
        }

        state.Update(status => status with { IsLeader = false });
    }

    private static string SafeCorrelationId(string envelope)
    {
        try
        {
            return KafkaOutboxPublisher.CorrelationIdOf(envelope);
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>When the head row may be attempted again.</summary>
    private readonly record struct RetryGate(long Sequence, int Attempts, DateTimeOffset DueAt);

    [LoggerMessage(EventId = 3300, Level = LogLevel.Warning, Message = "The outbox relay is disabled: TenantMerchant:Kafka:BootstrapServers is not set (Development only). Committed events stay pending in OutboxMessages")]
    private static partial void LogRelayDisabled(ILogger logger);

    [LoggerMessage(EventId = 3301, Level = LogLevel.Information, Message = "The outbox relay acquired the leadership and publishes")]
    private static partial void LogLeadershipAcquired(ILogger logger);

    [LoggerMessage(EventId = 3302, Level = LogLevel.Warning, Message = "The outbox relay lost the leadership and stopped publishing")]
    private static partial void LogLeadershipLost(ILogger logger);

    [LoggerMessage(EventId = 3303, Level = LogLevel.Information, Message = "The outbox relay released the leadership on shutdown")]
    private static partial void LogLeadershipReleased(ILogger logger);

    [LoggerMessage(EventId = 3304, Level = LogLevel.Warning, Message = "The outbox relay could not check its leadership ({ExceptionType})")]
    private static partial void LogLeadershipCheckFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3310, Level = LogLevel.Debug, Message = "The outbox relay published {Count} messages")]
    private static partial void LogBatchPublished(ILogger logger, int count);

    [LoggerMessage(
        EventId = 3311,
        Level = LogLevel.Warning,
        Message = "Publishing {EventType} {EventId} v{EventVersion} of tenant {TenantId} (correlation {CorrelationId}) failed: {FailureCategory} {ErrorCode}, attempt {Attempts}; retrying in {BackoffSeconds} s")]
    private static partial void LogPublishFailed(ILogger logger, string eventType, Guid eventId, int eventVersion, string tenantId, string correlationId, string failureCategory, string errorCode, int attempts, double backoffSeconds);

    [LoggerMessage(
        EventId = 3312,
        Level = LogLevel.Error,
        Message = "The outbox relay is halted at {EventType} {EventId} v{EventVersion} of tenant {TenantId} (correlation {CorrelationId}): {FailureCategory} {ErrorCode} after {Attempts} attempts. Nothing after it is published until the cause is fixed (runbook: TENANT_MERCHANT_EVENT_RELAY.md)")]
    private static partial void LogHalted(ILogger logger, string eventType, Guid eventId, int eventVersion, string tenantId, string correlationId, string failureCategory, string errorCode, int attempts);

    [LoggerMessage(
        EventId = 3313,
        Level = LogLevel.Error,
        Message = "The outbox relay is still halted at {EventType} {EventId} of tenant {TenantId}: {FailureCategory} {ErrorCode} after {Attempts} attempts")]
    private static partial void LogStillHalted(ILogger logger, string eventType, Guid eventId, string tenantId, string failureCategory, string errorCode, int attempts);

    [LoggerMessage(EventId = 3314, Level = LogLevel.Error, Message = "The outbox relay cannot publish: topic {Topic} is {TopicStatus}; it is never created by the relay. Retrying in {BackoffSeconds} s")]
    private static partial void LogTopicUnavailable(ILogger logger, string topic, string topicStatus, double backoffSeconds);

    [LoggerMessage(EventId = 3315, Level = LogLevel.Warning, Message = "An outbox relay cycle failed ({ExceptionType}), {Failures} in a row; retrying in {BackoffSeconds} s")]
    private static partial void LogCycleFailed(ILogger logger, string exceptionType, int failures, double backoffSeconds);

    [LoggerMessage(EventId = 3320, Level = LogLevel.Information, Message = "The outbox cleanup deleted {Count} published messages older than {RetentionDays} days")]
    private static partial void LogCleanedUp(ILogger logger, int count, double retentionDays);

    [LoggerMessage(EventId = 3321, Level = LogLevel.Debug, Message = "The outbox backlog could not be read ({ExceptionType})")]
    private static partial void LogBacklogReadFailed(ILogger logger, string exceptionType);
}
