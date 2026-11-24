using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>An in-memory outbox for relay logic only; the SQL Server behaviour is proven in the integration tests.</summary>
internal sealed class FakeOutboxStore : IOutboxStore
{
    public List<Row> Rows { get; } = [];

    public List<IReadOnlyList<long>> MarkCalls { get; } = [];

    public List<(long Sequence, string Error)> Failures { get; } = [];

    public List<DateTimeOffset> CleanupCutoffs { get; } = [];

    /// <summary>Thrown by the next read, once.</summary>
    public Exception? FailNextRead { get; set; }

    /// <summary>How many rows each cleanup statement reports deleted, in order (0 when exhausted).</summary>
    public Queue<int> CleanupResults { get; } = new();

    public long Add(int attempts = 0, DateTimeOffset? lastAttemptAt = null, string? key = null)
    {
        var sequence = Rows.Count + 1;
        var eventId = Guid.CreateVersion7();
        var tenant = key ?? Guid.CreateVersion7().ToString("D");
        Rows.Add(new Row(sequence, eventId, tenant, attempts, lastAttemptAt)
        {
            Envelope = $$"""{"eventId":"{{eventId}}","correlationId":"corr-{{sequence}}"}""",
        });
        return sequence;
    }

    public Task<IReadOnlyList<PendingOutboxMessage>> ReadPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (FailNextRead is { } failure)
        {
            FailNextRead = null;
            throw failure;
        }

        IReadOnlyList<PendingOutboxMessage> batch =
        [
            .. Rows.Where(row => row.PublishedAt is null)
                .OrderBy(row => row.Sequence)
                .Take(batchSize)
                .Select(row => new PendingOutboxMessage(row.Sequence, row.EventId, "tenant-merchant.events", row.Key, "TenantCreated", 1, row.Envelope, DateTimeOffset.UnixEpoch, row.Attempts, row.LastAttemptAt)),
        ];
        return Task.FromResult(batch);
    }

    public Task MarkPublishedAsync(IReadOnlyList<long> sequences, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MarkCalls.Add([.. sequences]);
        foreach (var row in Rows.Where(row => sequences.Contains(row.Sequence) && row.PublishedAt is null))
        {
            row.PublishedAt = publishedAt;
        }

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(long sequence, DateTimeOffset attemptedAt, string error, CancellationToken cancellationToken)
    {
        var row = Rows.Single(candidate => candidate.Sequence == sequence);
        row.Attempts++;
        row.LastAttemptAt = attemptedAt;
        Failures.Add((sequence, error));
        return Task.CompletedTask;
    }

    public Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken)
    {
        var pending = Rows.Where(row => row.PublishedAt is null).ToList();
        return Task.FromResult(new OutboxBacklog(pending.Count, pending.Count == 0 ? null : DateTimeOffset.UnixEpoch));
    }

    public Task<int> DeletePublishedBeforeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken)
    {
        CleanupCutoffs.Add(cutoff);
        return Task.FromResult(CleanupResults.TryDequeue(out var deleted) ? deleted : 0);
    }

    internal sealed class Row(long sequence, Guid eventId, string key, int attempts, DateTimeOffset? lastAttemptAt)
    {
        public long Sequence { get; } = sequence;

        public Guid EventId { get; } = eventId;

        public string Key { get; } = key;

        public string Envelope { get; init; } = "{}";

        public int Attempts { get; set; } = attempts;

        public DateTimeOffset? LastAttemptAt { get; set; } = lastAttemptAt;

        public DateTimeOffset? PublishedAt { get; set; }
    }
}

/// <summary>A publisher that records what it was asked to send and answers from a script.</summary>
internal sealed class FakeOutboxPublisher : IOutboxPublisher
{
    public List<long> Published { get; } = [];

    public TopicStatus Topic { get; set; } = TopicStatus.Exists;

    public int TopicChecks { get; private set; }

    /// <summary>Decides the result per row; delivered by default.</summary>
    public Func<PendingOutboxMessage, PublishResult> Respond { get; set; } = _ => PublishResult.Success;

    /// <summary>Runs before the answer, inside the "in-flight" delivery.</summary>
    public Action<PendingOutboxMessage>? DuringDelivery { get; set; }

    public bool Disposed { get; private set; }

    public Task<PublishResult> PublishAsync(PendingOutboxMessage message, CancellationToken cancellationToken)
    {
        DuringDelivery?.Invoke(message);
        var result = Respond(message);
        if (result.Delivered)
        {
            Published.Add(message.Sequence);
        }

        return Task.FromResult(result);
    }

    public Task<TopicStatus> CheckTopicAsync(string topic, CancellationToken cancellationToken)
    {
        TopicChecks++;
        return Task.FromResult(Topic);
    }

    public void Dispose() => Disposed = true;
}

/// <summary>A leadership that is granted or not, as the test says.</summary>
internal sealed class FakeOutboxLeadership : IOutboxLeadership
{
    public bool Granted { get; set; } = true;

    public bool Released { get; private set; }

    public Task<bool> TryEnsureAsync(CancellationToken cancellationToken) => Task.FromResult(Granted);

    public Task ReleaseAsync()
    {
        Released = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
