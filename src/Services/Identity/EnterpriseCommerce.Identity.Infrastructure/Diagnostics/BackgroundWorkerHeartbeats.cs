namespace EnterpriseCommerce.Identity.Infrastructure.Diagnostics;

/// <summary>The last run of one periodic background worker of this instance, for readiness reporting.</summary>
public sealed class WorkerHeartbeat(DateTimeOffset startedAt)
{
    private long _lastRunTicks;
    private long _lastSuccessTicks;

    /// <summary>When the process (and with it the worker) started.</summary>
    public DateTimeOffset StartedAt { get; } = startedAt;

    /// <summary>When the last run ended, whatever its outcome.</summary>
    public DateTimeOffset? LastRunAt => Read(ref _lastRunTicks);

    /// <summary>When the last run ended without failing.</summary>
    public DateTimeOffset? LastSuccessAt => Read(ref _lastSuccessTicks);

    public void Record(bool succeeded, DateTimeOffset at)
    {
        Interlocked.Exchange(ref _lastRunTicks, at.UtcTicks);
        if (succeeded)
        {
            Interlocked.Exchange(ref _lastSuccessTicks, at.UtcTicks);
        }
    }

    /// <summary>
    /// Whether the worker has gone too long without a successful run: <paramref name="allowance"/> after the last
    /// success, or <paramref name="firstRunDue"/> plus the allowance after startup when it never succeeded.
    /// </summary>
    public bool IsOverdue(DateTimeOffset now, TimeSpan firstRunDue, TimeSpan allowance) =>
        LastSuccessAt is { } success ? now - success > allowance : now - StartedAt > firstRunDue + allowance;

    private static DateTimeOffset? Read(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTimeOffset(value, TimeSpan.Zero);
    }
}

/// <summary>Heartbeats of Identity's periodic workers (Phase 2.3 S6.5).</summary>
public sealed class BackgroundWorkerHeartbeats(TimeProvider timeProvider)
{
    public WorkerHeartbeat Retention { get; } = new(timeProvider.GetUtcNow());

    public WorkerHeartbeat SecurityEmailReconciliation { get; } = new(timeProvider.GetUtcNow());
}
