using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.Infrastructure.Security;

/// <summary>
/// Post-commit OpenIddict bulk revocation (ADR-017 section 8). Idempotent: revoking what is already revoked
/// changes nothing. Deadlocks are retried here, separately from the authoritative transaction. Any other failure
/// is logged and reported as <see cref="CleanupOutcome.Failed"/>; the committed security version still blocks
/// every affected session.
/// </summary>
internal sealed partial class SessionRevocationCleanup(
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityTransactionOptions> options,
    ILogger<SessionRevocationCleanup> logger) : ISessionRevocationCleanup
{
    public Task<CleanupOutcome> RevokeAllForSubjectAsync(string subject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return RunAsync(
            "revoke-subject",
            async (services, ct) =>
            {
                await services.GetRequiredService<IOpenIddictAuthorizationManager>().RevokeBySubjectAsync(subject, ct);
                await services.GetRequiredService<IOpenIddictTokenManager>().RevokeBySubjectAsync(subject, ct);
            },
            cancellationToken);
    }

    public Task<CleanupOutcome> RevokeAuthorizationTokensAsync(string authorizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);

        return RunAsync(
            "revoke-authorization-tokens",
            async (services, ct) =>
                await services.GetRequiredService<IOpenIddictTokenManager>().RevokeByAuthorizationIdAsync(authorizationId, ct),
            cancellationToken);
    }

    private async Task<CleanupOutcome> RunAsync(
        string operation,
        Func<IServiceProvider, CancellationToken, Task> cleanup,
        CancellationToken cancellationToken)
    {
        var maxAttempts = options.Value.CleanupMaxAttempts;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await cleanup(scope.ServiceProvider, cancellationToken);
                return CleanupOutcome.Completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (SqlServerErrors.IsDeadlock(exception) && attempt < maxAttempts)
            {
                IdentitySecurityMetrics.CleanupDeadlockRetries.Add(1, new KeyValuePair<string, object?>("operation", operation));
                LogCleanupDeadlockRetry(logger, operation, attempt, maxAttempts);
                await Task.Delay(options.Value.DelayBeforeAttempt(attempt + 1), cancellationToken);
            }
            catch (Exception exception)
            {
                // Deliberately broad: a cleanup failure must never surface as a failure of the committed decision.
                IdentitySecurityMetrics.CleanupFailures.Add(1, new KeyValuePair<string, object?>("operation", operation));
                LogCleanupFailed(logger, exception, operation, attempt);
                return CleanupOutcome.Failed;
            }
        }
    }

    [LoggerMessage(
        EventId = 2010,
        Level = LogLevel.Warning,
        Message = "Post-commit cleanup {Operation} deadlocked on attempt {Attempt} of {MaxAttempts}; retrying")]
    private static partial void LogCleanupDeadlockRetry(ILogger logger, string operation, int attempt, int maxAttempts);

    [LoggerMessage(
        EventId = 2011,
        Level = LogLevel.Error,
        Message = "Post-commit cleanup {Operation} failed on attempt {Attempt}; the committed security decision stands")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception, string operation, int attempt);
}
