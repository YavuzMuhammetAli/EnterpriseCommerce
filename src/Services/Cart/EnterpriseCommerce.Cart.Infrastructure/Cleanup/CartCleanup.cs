using EnterpriseCommerce.Cart.Application.Common;
using EnterpriseCommerce.Cart.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Cart.Infrastructure.Cleanup;

/// <summary>The expired-cart cleanup (ADR-029 section 9). Configuration section <c>Cart:Cleanup</c>.</summary>
internal sealed class CartCleanupOptions
{
    public const string SectionName = "Cart:Cleanup";

    public bool Enabled { get; set; } = true;

    /// <summary>The time between cycles, with up to 10 % jitter. 10 seconds to 24 hours.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Carts deleted per statement. 1 to 1,000.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Statements per cycle, so one cycle stays short. 1 to 100.</summary>
    public int MaxBatchesPerCycle { get; set; } = 20;

    public IEnumerable<string> Validate()
    {
        if (Interval < TimeSpan.FromSeconds(10) || Interval > TimeSpan.FromHours(24))
        {
            yield return $"{SectionName}:Interval must be 10 seconds to 24 hours.";
        }

        if (BatchSize is < 1 or > 1000)
        {
            yield return $"{SectionName}:BatchSize must be 1 to 1000.";
        }

        if (MaxBatchesPerCycle is < 1 or > 100)
        {
            yield return $"{SectionName}:MaxBatchesPerCycle must be 1 to 100.";
        }
    }
}

/// <summary>The last cycles of the cleanup on this instance, for its health check.</summary>
internal sealed class CartCleanupState(TimeProvider time)
{
    private long _lastSuccessTicks = time.GetUtcNow().UtcTicks;

    public DateTimeOffset LastSuccess => new(Interlocked.Read(ref _lastSuccessTicks), TimeSpan.Zero);

    public void RecordSuccess(DateTimeOffset at) => Interlocked.Exchange(ref _lastSuccessTicks, at.UtcTicks);
}

/// <summary>
/// Deletes expired carts in short batches: <c>DELETE TOP (n)</c> of carts unchanged since the retention cutoff, their
/// lines by cascade. Deleting is idempotent, so every instance may run it without a leader: two instances that pick the
/// same cart delete it once, and a cart that a customer changes in the meantime is no longer expired.
/// </summary>
internal sealed partial class CartCleanupRunner(
    IServiceScopeFactory scopes,
    TimeProvider time,
    CartLifecycleSettings lifecycle,
    IOptions<CartCleanupOptions> options,
    CartCleanupState state,
    ILogger<CartCleanupRunner> logger)
{
    /// <summary>Runs one cycle and returns the number of carts deleted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var cutoff = time.GetUtcNow() - lifecycle.InactiveCartRetention;
        var deleted = 0;

        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CartDbContext>();
        for (var batch = 0; batch < settings.MaxBatchesPerCycle; batch++)
        {
            // The UpdatedAt condition is part of the statement, so a cart changed after the cutoff is never deleted.
            var count = await context.Database.ExecuteSqlAsync(
                $"DELETE TOP ({settings.BatchSize}) FROM [Carts] WHERE [UpdatedAt] <= {cutoff}",
                cancellationToken);
            deleted += count;
            if (count < settings.BatchSize)
            {
                break;
            }
        }

        if (deleted > 0)
        {
            CartMetrics.RecordCartsExpired(deleted);
            LogDeleted(logger, deleted);
        }

        state.RecordSuccess(time.GetUtcNow());
        return deleted;
    }

    [LoggerMessage(EventId = 5040, Level = LogLevel.Information, Message = "Cart cleanup deleted {Count} expired carts")]
    private static partial void LogDeleted(ILogger logger, int count);
}

/// <summary>Runs the cleanup on every instance, every interval with jitter, until the host stops.</summary>
internal sealed partial class CartCleanupWorker(
    CartCleanupRunner runner,
    IOptions<CartCleanupOptions> options,
    TimeProvider time,
    ILogger<CartCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = options.Value.Interval;
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * interval.TotalMilliseconds * 0.1);
            try
            {
                await Task.Delay(interval + jitter, time, stoppingToken);
                await runner.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (SqlServerErrors.IsSqlServerFailure(exception))
            {
                // A database outage only delays the cleanup: expired carts read as empty anyway.
                LogCycleFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(EventId = 5041, Level = LogLevel.Warning, Message = "Cart cleanup cycle failed; retrying at the next interval")]
    private static partial void LogCycleFailed(ILogger logger, Exception exception);
}

/// <summary>
/// <c>/health/cleanup</c>: the cleanup completed a cycle within three intervals. Never part of readiness, because an
/// expired cart reads as empty whether or not it was deleted.
/// </summary>
internal sealed class CartCleanupHealthCheck(IOptions<CartCleanupOptions> options, CartCleanupState state, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var healthy = !settings.Enabled || time.GetUtcNow() - state.LastSuccess <= settings.Interval * 3;
        return Task.FromResult(healthy ? HealthCheckResult.Healthy() : new HealthCheckResult(context.Registration.FailureStatus, "The cart cleanup is behind."));
    }
}
