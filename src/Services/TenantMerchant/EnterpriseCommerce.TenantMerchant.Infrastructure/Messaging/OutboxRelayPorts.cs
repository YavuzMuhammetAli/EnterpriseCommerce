namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

/// <summary>A committed, unpublished outbox row, as the relay reads it.</summary>
internal sealed record PendingOutboxMessage(
    long Sequence,
    Guid EventId,
    string Topic,
    string MessageKey,
    string EventType,
    int EventVersion,
    string Envelope,
    DateTimeOffset OccurredAt,
    int Attempts,
    DateTimeOffset? LastAttemptAt);

/// <summary>The pending backlog: its size and the business time of its oldest row.</summary>
internal readonly record struct OutboxBacklog(long Count, DateTimeOffset? OldestOccurredAt);

/// <summary>
/// Why a publication failed: a bounded set, safe as a metric tag and in <c>LastError</c>. Only
/// <see cref="Transient"/> is expected to heal by itself; the others need an operator (ADR-023 section 6).
/// </summary>
internal enum PublishFailureCategory
{
    /// <summary>Broker, network or timeout: retried with backoff.</summary>
    Transient,

    /// <summary>The topic or its partition does not exist. The relay never creates a topic.</summary>
    Topic,

    /// <summary>The producer is not allowed to write the topic, or could not authenticate.</summary>
    Authorization,

    /// <summary>The broker refused the message itself, for example its size.</summary>
    Rejected,

    /// <summary>The client configuration is invalid, or the producer reached a fatal state.</summary>
    Configuration,

    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>The outcome of one publication: delivered, or a categorized failure with the broker's error code.</summary>
internal readonly record struct PublishResult(bool Delivered, PublishFailureCategory Category, string Code)
{
    public static PublishResult Success { get; } = new(true, PublishFailureCategory.Transient, string.Empty);

    /// <summary>Whether the failure is not expected to heal by itself: the relay reports itself halted at once.</summary>
    public bool IsPermanent => !Delivered && Category is not PublishFailureCategory.Transient and not PublishFailureCategory.Unknown;

    public static PublishResult Failure(PublishFailureCategory category, string code) => new(false, category, code);

    /// <summary>The value stored in <c>LastError</c>: the category and the code, never the message or the payload.</summary>
    public string Describe() => $"{Category}:{Code}";
}

/// <summary>Whether the relay's topic exists.</summary>
internal enum TopicStatus
{
    Unknown,
    Exists,
    Missing,
    Unauthorized,
    Unreachable,
}

/// <summary>The relay's access to <c>OutboxMessages</c>. Every call is one short statement; no transaction spans a publication.</summary>
internal interface IOutboxStore
{
    /// <summary>Up to <paramref name="batchSize"/> committed, unpublished rows in <c>Sequence</c> order.</summary>
    Task<IReadOnlyList<PendingOutboxMessage>> ReadPendingAsync(int batchSize, CancellationToken cancellationToken);

    /// <summary>Marks the delivered rows published, in one statement. A row already marked stays as it is.</summary>
    Task MarkPublishedAsync(IReadOnlyList<long> sequences, DateTimeOffset publishedAt, CancellationToken cancellationToken);

    /// <summary>Records a failed attempt on a row: <c>Attempts + 1</c>, the time, and the category and code.</summary>
    Task RecordFailureAsync(long sequence, DateTimeOffset attemptedAt, string error, CancellationToken cancellationToken);

    Task<OutboxBacklog> GetBacklogAsync(CancellationToken cancellationToken);

    /// <summary>Deletes up to <paramref name="batchSize"/> published rows published before the cutoff; never a pending row.</summary>
    Task<int> DeletePublishedBeforeAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken);
}

/// <summary>Publishes one outbox row and waits for the broker's acknowledgement. Kafka types stay behind it.</summary>
internal interface IOutboxPublisher : IDisposable
{
    /// <summary>Produces the row and awaits its delivery report. Never throws for a broker failure.</summary>
    Task<PublishResult> PublishAsync(PendingOutboxMessage message, CancellationToken cancellationToken);

    /// <summary>Whether the topic exists, read from the broker's metadata. Never creates it.</summary>
    Task<TopicStatus> CheckTopicAsync(string topic, CancellationToken cancellationToken);
}

/// <summary>Leader election of the relay: one publishing instance per service (ADR-023 section 5).</summary>
internal interface IOutboxLeadership : IAsyncDisposable
{
    /// <summary>Whether this instance holds the leadership after the call: kept if still held, acquired if free.</summary>
    Task<bool> TryEnsureAsync(CancellationToken cancellationToken);

    /// <summary>Gives the leadership up.</summary>
    Task ReleaseAsync();
}

/// <summary>The exponential backoff of ADR-023 section 6: 1 second doubled per attempt, capped, with jitter.</summary>
internal static class OutboxBackoff
{
    /// <summary>The wait before attempt <paramref name="attempts"/> + 1: between 50 % and 100 % of the capped exponential value.</summary>
    public static TimeSpan For(int attempts, TimeSpan maxBackoff, double jitter)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 30);
        var capped = Math.Min(OutboxOptions.BaseBackoff.TotalMilliseconds * Math.Pow(2, exponent), maxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(capped * (0.5 + (0.5 * Math.Clamp(jitter, 0, 1))));
    }
}
