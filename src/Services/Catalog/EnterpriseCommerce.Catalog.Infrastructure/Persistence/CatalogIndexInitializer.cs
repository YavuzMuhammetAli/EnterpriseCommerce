using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// Creates the indexes in the background (ADR-024 section 10), so the host starts and stays live without MongoDB.
/// A transient failure (MongoDB unreachable, timing out) is retried with capped exponential backoff. An index
/// definition conflict is permanent: it is logged as an error and the service stays not ready, because a retry cannot
/// fix it and writing without the intended unique indexes would be unsafe.
/// </summary>
internal sealed partial class CatalogIndexInitializer(
    CatalogMongoContext context,
    CatalogIndexState state,
    ILogger<CatalogIndexInitializer> logger) : BackgroundService
{
    /// <summary>MongoDB codes of index definition conflicts, and duplicate keys met while building a unique index.</summary>
    private static readonly HashSet<int> PermanentCodes = [85, 86, 11000];

    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = FirstDelay;
        for (var attempt = 1; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await CatalogIndexes.EnsureAsync(context, stoppingToken);
                state.MarkReady();
                LogIndexesReady(logger, attempt);
                return;
            }
            catch (MongoCommandException exception) when (PermanentCodes.Contains(exception.Code))
            {
                state.MarkFailed();
                LogIndexConflict(logger, exception.Code, exception.CodeName);
                return;
            }
            catch (Exception exception) when (exception is MongoException or TimeoutException && !stoppingToken.IsCancellationRequested)
            {
                LogIndexSetupRetry(logger, exception.GetType().Name, attempt, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
        }
    }

    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Catalog indexes ensured after {Attempts} attempt(s); writes are accepted")]
    private static partial void LogIndexesReady(ILogger logger, int attempts);

    // The exception message is not logged: duplicate-key messages contain stored values.
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Error,
        Message = "Catalog index setup failed permanently with MongoDB error {Code} ({CodeName}); the service stays not ready and refuses writes")]
    private static partial void LogIndexConflict(ILogger logger, int code, string codeName);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Warning,
        Message = "Catalog index setup attempt {Attempt} failed ({ErrorType}); retrying in {DelaySeconds} s")]
    private static partial void LogIndexSetupRetry(ILogger logger, string errorType, int attempt, double delaySeconds);
}
