using System.Data;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.Infrastructure.Retention;

/// <summary>Outcome of one retention run.</summary>
public enum RetentionRunOutcome
{
    /// <summary>Every category was processed (up to the per-run batch limit).</summary>
    Completed,

    /// <summary>Another Identity instance holds the cleanup lock; nothing was done.</summary>
    Skipped,

    /// <summary>At least one category failed after its retries. The others were still processed.</summary>
    Failed,
}

/// <summary>Result of one retention run: the outcome and the deleted rows per category.</summary>
public sealed record RetentionRunResult(RetentionRunOutcome Outcome, long LedgerRows, long OpenIddictTokens, long OpenIddictAuthorizations);

/// <summary>
/// One retention run (Phase 2.3 S6). Deletes only records that can no longer reject any token:
/// <list type="number">
/// <item>refresh-token redemption ledger rows older than the replay protection window, in bounded batches that wait
/// for a row lock at most <see cref="IdentityRetentionOptions.LockTimeout"/>;</item>
/// <item>OpenIddict tokens, then authorizations, through the OpenIddict prune APIs (ADR-017 section 12, D18) with a
/// threshold of the database clock minus the same window. OpenIddict 7.9 deletes in autocommitted batches of 1000
/// (finding I32).</item>
/// </list>
/// Expired account security tokens and finished security email deliveries are deliberately not deleted: their
/// retention period is an open decision (docs/security/IDENTITY_OPERATIONS.md).
/// </summary>
/// <remarks>
/// Instances coordinate through a session-owned SQL Server application lock: one instance runs, every other one
/// skips the run. Every statement is idempotent and its own short transaction, and transient failures are retried a
/// bounded number of times, so a cancelled or failed run leaves nothing half-done.
/// </remarks>
internal sealed partial class IdentityRetentionRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityDatabaseOptions> database,
    IOptions<IdentityRetentionOptions> options,
    BackgroundWorkerHeartbeats heartbeats,
    TimeProvider timeProvider,
    ILogger<IdentityRetentionRunner> logger)
{
    /// <summary>The application lock that elects one runner across instances.</summary>
    public const string LockResource = "EnterpriseCommerce.Identity.Retention";

    public async Task<RetentionRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var result = await RunLockedAsync(settings, cancellationToken);

        heartbeats.Retention.Record(result.Outcome != RetentionRunOutcome.Failed, timeProvider.GetUtcNow());
        IdentityMetrics.RetentionRuns.Add(1, new KeyValuePair<string, object?>("outcome", OutcomeTag(result.Outcome)));
        Record("refresh-redemption-ledger", result.LedgerRows);
        Record("openiddict-tokens", result.OpenIddictTokens);
        Record("openiddict-authorizations", result.OpenIddictAuthorizations);
        LogRun(logger, result.Outcome, result.LedgerRows, result.OpenIddictTokens, result.OpenIddictAuthorizations);
        return result;
    }

    private async Task<RetentionRunResult> RunLockedAsync(IdentityRetentionOptions settings, CancellationToken cancellationToken)
    {
        SqlConnection lockConnection;
        try
        {
            // Unpooled: closing the connection ends the session, so the session-owned lock can never outlive the run in
            // a pooled connection, even if the explicit release below fails.
            var connectionString = new SqlConnectionStringBuilder(database.Value.ConnectionString) { Pooling = false }.ConnectionString;
            lockConnection = new SqlConnection(connectionString);
            await lockConnection.OpenAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCategoryFailed(logger, "lock", exception.GetType().Name);
            return new RetentionRunResult(RetentionRunOutcome.Failed, 0, 0, 0);
        }

        await using (lockConnection)
        {
            bool acquired;
            try
            {
                acquired = await TryAcquireLockAsync(lockConnection, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogCategoryFailed(logger, "lock", exception.GetType().Name);
                return new RetentionRunResult(RetentionRunOutcome.Failed, 0, 0, 0);
            }

            if (!acquired)
            {
                return new RetentionRunResult(RetentionRunOutcome.Skipped, 0, 0, 0);
            }

            try
            {
                return await RunCategoriesAsync(settings, cancellationToken);
            }
            finally
            {
                await ReleaseLockAsync(lockConnection);
            }
        }
    }

    private async Task<RetentionRunResult> RunCategoriesAsync(IdentityRetentionOptions settings, CancellationToken cancellationToken)
    {
        var windowSeconds = (int)Math.Ceiling(settings.ReplayProtectionWindow.TotalSeconds);

        var ledger = await DeleteLedgerAsync(settings, windowSeconds, cancellationToken);

        var threshold = await RetryAsync("openiddict-threshold", settings, () => ReadThresholdAsync(windowSeconds, cancellationToken), cancellationToken);
        long? tokens = null;
        long? authorizations = null;
        if (threshold is { } value)
        {
            // Tokens first: OpenIddict prunes an authorization only once no token refers to it.
            tokens = await RetryAsync("openiddict-tokens", settings, () => PruneTokensAsync(value, cancellationToken), cancellationToken);
            authorizations = await RetryAsync("openiddict-authorizations", settings, () => PruneAuthorizationsAsync(value, cancellationToken), cancellationToken);
        }

        var failed = ledger is null || threshold is null || tokens is null || authorizations is null;
        return new RetentionRunResult(failed ? RetentionRunOutcome.Failed : RetentionRunOutcome.Completed, ledger ?? 0, tokens ?? 0, authorizations ?? 0);
    }

    private async Task ReleaseLockAsync(SqlConnection connection)
    {
        try
        {
            await using var command = new SqlCommand("sp_releaseapplock", connection) { CommandType = CommandType.StoredProcedure };
            command.Parameters.AddWithValue("@Resource", LockResource);
            command.Parameters.AddWithValue("@LockOwner", "Session");
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // Closing the unpooled connection ends the session and releases the lock anyway.
            LogCategoryFailed(logger, "lock-release", exception.GetType().Name);
        }
    }

    private static async Task<bool> TryAcquireLockAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("sp_getapplock", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Resource", LockResource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", 0);
        var returnValue = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnValue.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(cancellationToken);

        // 0: granted, 1: granted after waiting. Negative: not granted (timeout, deadlock, error).
        return (int)returnValue.Value >= 0;
    }

    /// <summary>Deletes expired ledger rows in batches. Returns the count, or <see langword="null"/> after a failure.</summary>
    private async Task<long?> DeleteLedgerAsync(IdentityRetentionOptions settings, int windowSeconds, CancellationToken cancellationToken)
    {
        long total = 0;
        for (var batch = 0; batch < settings.MaxBatchesPerRun; batch++)
        {
            var deleted = await RetryAsync(
                "refresh-redemption-ledger",
                settings,
                async () =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();

                    // Database clock only. A row locked by an in-flight transaction is waited for at most LockTimeout
                    // (error 1222, retried as transient); the delete never removes uncommitted state. Expired ledger rows
                    // are insert-only, so in practice nothing else ever holds them.
                    var lockTimeout = (int)settings.LockTimeout.TotalMilliseconds;
                    return (long)await context.Database.ExecuteSqlAsync(
                        $"""
                        DECLARE @sql nvarchar(400) = N'SET LOCK_TIMEOUT ' + CAST({lockTimeout} AS nvarchar(12)) + N';
                        DELETE TOP (@batch) FROM [RefreshTokenRedemptions] WITH (ROWLOCK)
                        WHERE [RedeemedAt] < DATEADD(SECOND, -@window, SYSDATETIMEOFFSET());';
                        EXEC sp_executesql @sql, N'@batch int, @window int', @batch = {settings.BatchSize}, @window = {windowSeconds};
                        """,
                        cancellationToken);
                },
                cancellationToken);

            if (deleted is null)
            {
                return null;
            }

            total += deleted.Value;
            if (deleted.Value < settings.BatchSize)
            {
                break;
            }
        }

        return total;
    }

    private async Task<DateTimeOffset> ReadThresholdAsync(int windowSeconds, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        var now = await context.Database
            .SqlQuery<DateTime>($"SELECT DATEADD(SECOND, -{windowSeconds}, SYSUTCDATETIME()) AS [Value]")
            .SingleAsync(cancellationToken);
        return new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc));
    }

    private async Task<long> PruneTokensAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(threshold, cancellationToken);
    }

    private async Task<long> PruneAuthorizationsAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(threshold, cancellationToken);
    }

    /// <summary>Runs an idempotent step, retrying transient SQL failures a bounded number of times.</summary>
    private async Task<T?> RetryAsync<T>(string category, IdentityRetentionOptions settings, Func<Task<T>> step, CancellationToken cancellationToken)
        where T : struct
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await step();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (SqlServerErrors.IsTransient(exception) && attempt < settings.MaxAttempts)
            {
                IdentityMetrics.RetentionRetries.Add(1, new KeyValuePair<string, object?>("category", category));
                LogRetry(logger, category, attempt, settings.MaxAttempts);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt * attempt), timeProvider, cancellationToken);
            }
            catch (Exception exception)
            {
                // Deliberately broad: a failed category ends only this category of this run.
                LogCategoryFailed(logger, category, exception.GetType().Name);
                return null;
            }
        }
    }

    private static void Record(string category, long rows)
    {
        if (rows > 0)
        {
            IdentityMetrics.RetentionDeleted.Add(rows, new KeyValuePair<string, object?>("category", category));
        }
    }

    private static string OutcomeTag(RetentionRunOutcome outcome) => outcome switch
    {
        RetentionRunOutcome.Completed => "completed",
        RetentionRunOutcome.Skipped => "skipped",
        _ => "failed",
    };

    [LoggerMessage(
        EventId = 2200,
        Level = LogLevel.Information,
        Message = "Retention run {Outcome}: {LedgerRows} ledger rows, {Tokens} OpenIddict tokens, {Authorizations} OpenIddict authorizations deleted")]
    private static partial void LogRun(ILogger logger, RetentionRunOutcome outcome, long ledgerRows, long tokens, long authorizations);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning, Message = "Retention {Category} failed transiently on attempt {Attempt} of {MaxAttempts}; retrying")]
    private static partial void LogRetry(ILogger logger, string category, int attempt, int maxAttempts);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Error, Message = "Retention {Category} failed ({ExceptionType}); it is retried at the next run")]
    private static partial void LogCategoryFailed(ILogger logger, string category, string exceptionType);
}
