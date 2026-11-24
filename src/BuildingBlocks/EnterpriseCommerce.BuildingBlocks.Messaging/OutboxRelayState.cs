namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>What one relay instance knows about itself, for health and metrics. Immutable; replaced on every change.</summary>
/// <param name="Enabled">Whether the relay runs (Kafka is configured).</param>
/// <param name="IsLeader">Whether this instance holds the leadership.</param>
/// <param name="BackingOffUntil">Set while the relay waits after a failure.</param>
/// <param name="IsHalted">The head row keeps failing: a permanent failure, or <c>HaltAfterAttempts</c> attempts.</param>
/// <param name="Topic">The last topic check.</param>
/// <param name="LastFailure">The category of the last failed publication, until a publication succeeds.</param>
/// <param name="Backlog">The pending rows when last read.</param>
public sealed record OutboxRelayStatus(
    bool Enabled,
    bool IsLeader,
    DateTimeOffset? BackingOffUntil,
    bool IsHalted,
    TopicStatus Topic,
    PublishFailureCategory? LastFailure,
    OutboxBacklog Backlog)
{
    public static OutboxRelayStatus Initial { get; } = new(false, false, null, false, TopicStatus.Unknown, null, new OutboxBacklog(0, null));
}

/// <summary>
/// The relay's status, written by the relay only and read by the health check and the meters. It carries the service's
/// <see cref="OutboxMetrics"/>, so that the relay records into the meter of the service it runs in.
/// </summary>
public sealed class OutboxRelayState(OutboxMetrics metrics)
{
    private readonly Lock _gate = new();
    private volatile OutboxRelayStatus _current = OutboxRelayStatus.Initial;

    /// <summary>The meters this relay records into.</summary>
    public OutboxMetrics Metrics { get; } = metrics ?? throw new ArgumentNullException(nameof(metrics));

    public OutboxRelayStatus Current => _current;

    public void Update(Func<OutboxRelayStatus, OutboxRelayStatus> change)
    {
        lock (_gate)
        {
            _current = change(_current);
        }
    }
}
