using EnterpriseCommerce.Identity.Application.Security;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.Sessions;

/// <summary>Outcome of logout-all.</summary>
/// <param name="SecurityVersion">The committed security version. Every session established before it is invalid.</param>
/// <param name="CleanupCompleted">Whether the post-commit bulk revocation finished. Correctness never depends on it.</param>
public sealed record LogoutAllResult(long SecurityVersion, bool CleanupCompleted);

/// <summary>
/// Ends every session of a user (ADR-017 sections 6 to 8).
/// </summary>
/// <remarks>
/// The security-version increment is the authoritative change and commits in its own short transaction.
/// OpenIddict bulk revocation then runs as idempotent cleanup. If the increment cannot be committed, a
/// <see cref="SecurityStateUnavailableException"/> is thrown: logout-all never reports success without it.
/// </remarks>
public sealed partial class LogoutAllSessionsHandler(
    IAuthoritativeSecurityTransaction transaction,
    ISessionRevocationCleanup cleanup,
    ILogger<LogoutAllSessionsHandler> logger)
{
    public async Task<LogoutAllResult> HandleAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var newVersion = await transaction.ExecuteAsync(
            "logout-all",
            async (unit, ct) =>
            {
                var version = await unit.IncrementSecurityVersionAsync(userId, ct);
                return version is null
                    ? TransactionDecision.Rollback<long?>(null)
                    : TransactionDecision.Commit(version);
            },
            cancellationToken);

        if (newVersion is null)
        {
            LogMissingSecurityVersion(logger, userId);
            throw new SecurityStateUnavailableException("The security-version record of the user is missing.");
        }

        var cleanupOutcome = await cleanup.RevokeAllForSubjectAsync(userId, cancellationToken);
        LogLoggedOutEverywhere(logger, userId, newVersion.Value, cleanupOutcome);

        return new LogoutAllResult(newVersion.Value, cleanupOutcome == CleanupOutcome.Completed);
    }

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Error,
        Message = "Logout-all failed closed: no security-version record for subject {Subject}")]
    private static partial void LogMissingSecurityVersion(ILogger logger, string subject);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "All sessions of subject {Subject} ended at security version {SecurityVersion}; cleanup {CleanupOutcome}")]
    private static partial void LogLoggedOutEverywhere(ILogger logger, string subject, long securityVersion, CleanupOutcome cleanupOutcome);
}
