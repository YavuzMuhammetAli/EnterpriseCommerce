using System.Data;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.Infrastructure.Security;

/// <summary>
/// SQL Server implementation of <see cref="IAuthoritativeSecurityTransaction"/> (ADR-017 section 8).
/// Every attempt uses a new DI scope, so a fresh <see cref="IdentityServiceDbContext"/>, change tracker and
/// connection. A deadlock (1205) aborts the whole attempt and the work runs again from the start.
/// </summary>
internal sealed partial class AuthoritativeSecurityTransaction(
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityTransactionOptions> options,
    ILogger<AuthoritativeSecurityTransaction> logger) : IAuthoritativeSecurityTransaction
{
    public async Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Func<IAuthoritativeSecurityUnit, CancellationToken, Task<TransactionDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(work);

        var maxAttempts = options.Value.MaxAttempts;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ExecuteAttemptAsync(work, cancellationToken);
            }
            catch (Exception exception) when (SqlServerErrors.IsDeadlock(exception) && attempt < maxAttempts)
            {
                IdentitySecurityMetrics.TransactionDeadlockRetries.Add(1, new KeyValuePair<string, object?>("operation", operation));
                LogDeadlockRetry(logger, operation, attempt, maxAttempts);
                await Task.Delay(options.Value.DelayBeforeAttempt(attempt + 1), cancellationToken);
            }
            catch (Exception exception) when (SqlServerErrors.IsDeadlock(exception))
            {
                RecordFailure(operation, "deadlock-retries-exhausted");
                LogDeadlockRetriesExhausted(logger, exception, operation, attempt);
                throw new SecurityStateUnavailableException(
                    $"The authoritative transaction '{operation}' was deadlocked on every attempt.", exception);
            }
            catch (Exception exception) when (SqlServerErrors.IsSqlServerFailure(exception))
            {
                RecordFailure(operation, "database-failure");
                LogDatabaseFailure(logger, exception, operation, attempt);
                throw new SecurityStateUnavailableException(
                    $"The authoritative transaction '{operation}' could not be completed.", exception);
            }
        }
    }

    private async Task<TResult> ExecuteAttemptAsync<TResult>(
        Func<IAuthoritativeSecurityUnit, CancellationToken, Task<TransactionDecision<TResult>>> work,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var decision = await work(new AuthoritativeSecurityUnit(context, authorizations), cancellationToken);

        if (decision.ShouldCommit)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken);
        }

        return decision.Result;
    }

    private static void RecordFailure(string operation, string reason) =>
        IdentitySecurityMetrics.TransactionFailures.Add(
            1,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("reason", reason));

    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Warning,
        Message = "Authoritative transaction {Operation} deadlocked on attempt {Attempt} of {MaxAttempts}; retrying the whole transaction")]
    private static partial void LogDeadlockRetry(ILogger logger, string operation, int attempt, int maxAttempts);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Error,
        Message = "Authoritative transaction {Operation} failed closed: deadlocked on all {Attempts} attempts")]
    private static partial void LogDeadlockRetriesExhausted(ILogger logger, Exception exception, string operation, int attempts);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Authoritative transaction {Operation} failed closed on attempt {Attempt}: the database failed")]
    private static partial void LogDatabaseFailure(ILogger logger, Exception exception, string operation, int attempt);
}
