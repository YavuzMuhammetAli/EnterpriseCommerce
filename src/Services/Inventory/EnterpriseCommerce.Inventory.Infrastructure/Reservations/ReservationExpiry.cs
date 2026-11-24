using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Application.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.Infrastructure.Reservations;

/// <summary>The expiry worker's settings (<c>Inventory:ReservationExpiry</c>; ADR-026 section 7). Validated at startup.</summary>
internal sealed class ReservationExpiryOptions
{
    public const string SectionName = "Inventory:ReservationExpiry";

    /// <summary>Whether this instance runs the worker. Without it, commit and release still expire overdue reservations.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The idle interval between cycles (with up to 20 % jitter). A full batch starts the next cycle at once.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most reservations one cycle expires, each in its own short transaction.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>The <c>expiry</c> health check reports <c>Degraded</c> when a reservation stays overdue longer than this.</summary>
    public TimeSpan OverdueAfter { get; set; } = TimeSpan.FromMinutes(2);

    public IEnumerable<string> Validate()
    {
        if (Interval < TimeSpan.FromSeconds(1) || Interval > TimeSpan.FromMinutes(5))
        {
            yield return "Interval must be 1 second to 5 minutes.";
        }

        if (BatchSize is < 1 or > 500)
        {
            yield return "BatchSize must be 1 to 500.";
        }

        if (OverdueAfter < TimeSpan.FromSeconds(10) || OverdueAfter > TimeSpan.FromHours(1))
        {
            yield return "OverdueAfter must be 10 seconds to 1 hour.";
        }
    }
}

/// <summary>What this instance's worker last did, for health. Written by the worker only.</summary>
internal sealed class ReservationExpiryState(TimeProvider timeProvider)
{
    private long _lastCompletedTicks = timeProvider.GetUtcNow().UtcTicks;

    /// <summary>When the last cycle completed (or the instance started, before the first cycle).</summary>
    public DateTimeOffset LastCompletedAt => new(Interlocked.Read(ref _lastCompletedTicks), TimeSpan.Zero);

    public void Completed() => Interlocked.Exchange(ref _lastCompletedTicks, timeProvider.GetUtcNow().UtcTicks);
}

/// <summary>
/// One expiry cycle (ADR-026 section 7): claims the overdue active reservation of any tenant that expired longest ago,
/// one per short transaction, until none is due or the batch is full. A claim locks the reservation and skips rows other
/// transactions hold (<c>READPAST</c>), so several instances share the work and a reservation in a commit or release is
/// re-examined later. Each expiry returns its lines' stock and writes its movements and events in its own transaction.
/// </summary>
internal sealed partial class ReservationExpiryRunner(IInventoryWriteGate gate, IOptions<ReservationExpiryOptions> options, ILogger<ReservationExpiryRunner> logger)
{
    /// <summary>Expires up to one batch; returns how many reservations it expired.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var expired = 0;
        while (expired < options.Value.BatchSize && !cancellationToken.IsCancellationRequested)
        {
            if (!await ExpireOneAsync(cancellationToken))
            {
                break;
            }

            expired++;
        }

        if (expired > 0)
        {
            LogExpired(logger, expired);
        }

        return expired;
    }

    private Task<bool> ExpireOneAsync(CancellationToken cancellationToken) =>
        gate.ExecuteAsync(
            "expire-reservation",
            async (session, token) =>
            {
                var reservation = await session.ClaimOverdueReservationAsync(token);
                if (reservation is null)
                {
                    return WriteDecision.Rollback(false);
                }

                var items = await session.LockItemsAsync(reservation.TenantId, [.. reservation.Lines.Select(line => line.InventoryItemId)], token);

                // A correlation of its own: no request caused it. Its events are root messages (ADR-023 section 4).
                var correlationId = $"reservation-expiry-{reservation.Id:N}";
                var result = reservation.Expire(items, correlationId, session.Now);
                if (result.Outcome != TransitionOutcome.Changed)
                {
                    // Claimed as overdue by the database clock, so this cannot happen; never loop on it.
                    throw new InvalidOperationException("A claimed overdue reservation did not expire.");
                }

                session.UseMessageMetadata(MessageMetadata.ForRequest(correlationId)!);
                InventoryMetrics.RecordExpired("worker");
                return WriteDecision.Commit(true);
            },
            cancellationToken);

    [LoggerMessage(EventId = 5300, Level = LogLevel.Information, Message = "The reservation expiry expired {Count} reservations")]
    private static partial void LogExpired(ILogger logger, int count);
}

/// <summary>
/// The reservation expiry worker: a hosted service on every instance, never part of a request (ADR-026 section 7). It
/// keeps no state beyond its last completion, so a restart simply continues with the overdue reservations it finds.
/// On stop it takes no new claim; a claim in progress commits or rolls back.
/// </summary>
internal sealed partial class ReservationExpiryWorker(
    ReservationExpiryRunner runner,
    ReservationExpiryState state,
    IOptions<ReservationExpiryOptions> options,
    TimeProvider timeProvider,
    ILogger<ReservationExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        await Task.Yield();
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var expired = await runner.RunOnceAsync(stoppingToken);
                state.Completed();
                InventoryMetrics.RecordExpiryCycle("completed");
                failures = 0;

                // Drain at once while batches come back full; otherwise wait the interval with jitter.
                delay = expired >= settings.BatchSize ? TimeSpan.Zero : settings.Interval * (1 + (0.2 * Random.Shared.NextDouble()));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The database or anything unexpected: back off, bounded, and try again. Nothing is lost: the
                // reservation's transaction rolled back, and commit and release still expire it lazily.
                failures++;
                InventoryMetrics.RecordExpiryCycle("failed");
                LogCycleFailed(logger, exception.GetType().Name, failures);
                delay = settings.Interval * Math.Min(Math.Pow(2, failures), 12);
            }

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

    [LoggerMessage(EventId = 5301, Level = LogLevel.Warning, Message = "The reservation expiry worker is disabled on this instance; commit and release still expire overdue reservations")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Warning, Message = "A reservation expiry cycle failed ({ExceptionType}), {Failures} in a row; retrying with backoff")]
    private static partial void LogCycleFailed(ILogger logger, string exceptionType, int failures);
}

/// <summary>
/// The <c>expiry</c> health check (ADR-026 section 7): <c>Degraded</c> when an active reservation has stayed overdue
/// longer than <see cref="ReservationExpiryOptions.OverdueAfter"/>, read from the database so every instance agrees, or
/// when this instance's enabled worker has not completed a cycle within three intervals plus that bound. Never part of
/// readiness: commit and release expire overdue reservations themselves.
/// </summary>
internal sealed class ReservationExpiryHealthCheck(
    IInventoryQueries queries,
    ReservationExpiryState state,
    IOptions<ReservationExpiryOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (await queries.CountOverdueReservationsAsync(settings.OverdueAfter, cancellationToken) > 0)
        {
            return HealthCheckResult.Degraded("Active reservations stay overdue beyond the threshold.");
        }

        if (settings.Enabled && timeProvider.GetUtcNow() - state.LastCompletedAt > (settings.Interval * 3) + settings.OverdueAfter)
        {
            return HealthCheckResult.Degraded("The reservation expiry worker has not completed a cycle recently.");
        }

        return HealthCheckResult.Healthy();
    }
}
