using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Accounts;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.Accounts;

/// <summary>An administrative account status change, requested by a trusted service client on behalf of Admin.</summary>
/// <param name="AccountId">The account (user ID, <c>sub</c>) to change.</param>
/// <param name="Reason">The bounded reason code.</param>
/// <param name="ActorReference">Admin's opaque reference to the administrator. Recorded for audit, never authorizing.</param>
/// <param name="CallerClientId">The authenticated service client that called Identity.</param>
public sealed record AccountStatusChange(string AccountId, AccountStatusReason Reason, string ActorReference, string CallerClientId);

/// <summary>Outcome of an account lifecycle operation.</summary>
public enum AccountLifecycleOutcome
{
    /// <summary>The status was changed and committed.</summary>
    Changed,

    /// <summary>The account already had the requested status. Nothing was changed.</summary>
    Unchanged,

    /// <summary>No account has this ID.</summary>
    NotFound,
}

/// <summary>Result of an account lifecycle operation.</summary>
/// <param name="Status">The account status after the operation. <see langword="null"/> when not found.</param>
/// <param name="SecurityVersion">The committed security version, set only when a disablement raised it.</param>
public sealed record AccountLifecycleResult(AccountLifecycleOutcome Outcome, AccountStatus? Status, long? SecurityVersion)
{
    public static AccountLifecycleResult NotFound { get; } = new(AccountLifecycleOutcome.NotFound, null, null);
}

/// <summary>
/// Disables an account (ADR-017 sections 6 to 9, PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.6.6).
/// </summary>
/// <remarks>
/// <para>
/// One authoritative transaction changes the status from <c>Active</c> to <c>Disabled</c> (rotating the security stamp),
/// raises the security version and supersedes every outstanding security token of the user. Any failure rolls all of
/// it back. A missing security-version record fails closed. The raised version ends every session, the disabled status
/// refuses every future sign-in, code exchange and refresh.
/// </para>
/// <para>
/// After commit, every authorization and token of the user is revoked as idempotent cleanup. The cleanup also runs
/// when the account was already disabled, so repeating the request completes a cleanup that failed earlier.
/// </para>
/// </remarks>
public sealed partial class DisableAccountHandler(
    IAuthoritativeSecurityTransaction transaction,
    ISessionRevocationCleanup cleanup,
    TimeProvider timeProvider,
    ILogger<DisableAccountHandler> logger)
{
    public const string Operation = "account-disable";

    public async Task<AccountLifecycleResult> HandleAsync(AccountStatusChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.AccountId);
        var now = timeProvider.GetUtcNow();

        var attempt = await transaction.ExecuteAsync(
            Operation,
            async (unit, ct) =>
            {
                var account = await unit.ReadAccountStateAsync(change.AccountId, ct);
                if (account is null)
                {
                    return TransactionDecision.Rollback(new Attempt(Step.NotFound, null));
                }

                // Conditional on the current status: of two concurrent disablements, the second waits for the first
                // one's row lock, then finds the account already disabled and changes nothing.
                if (!await unit.TryChangeAccountStatusAsync(
                    change.AccountId, AccountStatus.Active, AccountStatus.Disabled, change.Reason, change.ActorReference, ct))
                {
                    return TransactionDecision.Rollback(new Attempt(Step.AlreadyDisabled, null));
                }

                var version = await unit.IncrementSecurityVersionAsync(change.AccountId, ct);
                if (version is null)
                {
                    return TransactionDecision.Rollback(new Attempt(Step.MissingSecurityVersion, null));
                }

                await unit.SupersedeSecurityTokensAsync(change.AccountId, purpose: null, now, ct);
                return TransactionDecision.Commit(new Attempt(Step.Committed, version));
            },
            cancellationToken);

        switch (attempt.Step)
        {
            case Step.NotFound:
                Record("disable", "not_found");
                return AccountLifecycleResult.NotFound;

            case Step.MissingSecurityVersion:
                Record("disable", "failed");
                LogMissingSecurityVersion(logger, change.AccountId);
                throw new SecurityStateUnavailableException("The security-version record of the user is missing.");
        }

        var changed = attempt.Step == Step.Committed;
        Record("disable", changed ? "changed" : "unchanged");
        var cleanupOutcome = await cleanup.RevokeAllForSubjectAsync(change.AccountId, cancellationToken);
        LogAccountDisabled(logger, change.AccountId, changed, change.Reason, change.CallerClientId, change.ActorReference, attempt.Version, cleanupOutcome);

        return new AccountLifecycleResult(changed ? AccountLifecycleOutcome.Changed : AccountLifecycleOutcome.Unchanged, AccountStatus.Disabled, attempt.Version);
    }

    internal static void Record(string action, string outcome) =>
        IdentityMetrics.AccountLifecycle.Add(1, IdentityMetrics.Tag("action", action), IdentityMetrics.Tag("outcome", outcome));

    private enum Step
    {
        Committed,
        AlreadyDisabled,
        NotFound,
        MissingSecurityVersion,
    }

    private readonly record struct Attempt(Step Step, long? Version);

    [LoggerMessage(
        EventId = 3500,
        Level = LogLevel.Warning,
        Message = "Security event AccountDisabled for subject {Subject} (changed {Changed}, reason {Reason}) by client {ClientId} for actor {ActorReference} at security version {SecurityVersion}; cleanup {CleanupOutcome}")]
    private static partial void LogAccountDisabled(
        ILogger logger,
        string subject,
        bool changed,
        AccountStatusReason reason,
        string clientId,
        string actorReference,
        long? securityVersion,
        CleanupOutcome cleanupOutcome);

    [LoggerMessage(EventId = 3501, Level = LogLevel.Error, Message = "Account disablement failed closed: no security-version record for subject {Subject}")]
    private static partial void LogMissingSecurityVersion(ILogger logger, string subject);
}

/// <summary>
/// Reactivates a disabled account (PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.6.6).
/// </summary>
/// <remarks>
/// Only the status changes. The security version keeps the value the disablement raised, and no authorization, refresh
/// token or Identity cookie from before the disablement becomes valid again: the user signs in again.
/// </remarks>
public sealed partial class ReactivateAccountHandler(
    IAuthoritativeSecurityTransaction transaction,
    ILogger<ReactivateAccountHandler> logger)
{
    public const string Operation = "account-reactivate";

    public async Task<AccountLifecycleResult> HandleAsync(AccountStatusChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.AccountId);

        var outcome = await transaction.ExecuteAsync(
            Operation,
            async (unit, ct) =>
            {
                if (await unit.ReadAccountStateAsync(change.AccountId, ct) is null)
                {
                    return TransactionDecision.Rollback(AccountLifecycleOutcome.NotFound);
                }

                return await unit.TryChangeAccountStatusAsync(
                        change.AccountId, AccountStatus.Disabled, AccountStatus.Active, change.Reason, change.ActorReference, ct)
                    ? TransactionDecision.Commit(AccountLifecycleOutcome.Changed)
                    : TransactionDecision.Rollback(AccountLifecycleOutcome.Unchanged);
            },
            cancellationToken);

        DisableAccountHandler.Record("reactivate", outcome switch
        {
            AccountLifecycleOutcome.Changed => "changed",
            AccountLifecycleOutcome.Unchanged => "unchanged",
            _ => "not_found",
        });

        if (outcome == AccountLifecycleOutcome.NotFound)
        {
            return AccountLifecycleResult.NotFound;
        }

        LogAccountReactivated(logger, change.AccountId, outcome == AccountLifecycleOutcome.Changed, change.Reason, change.CallerClientId, change.ActorReference);
        return new AccountLifecycleResult(outcome, AccountStatus.Active, null);
    }

    [LoggerMessage(
        EventId = 3502,
        Level = LogLevel.Warning,
        Message = "Security event AccountReactivated for subject {Subject} (changed {Changed}, reason {Reason}) by client {ClientId} for actor {ActorReference}")]
    private static partial void LogAccountReactivated(
        ILogger logger,
        string subject,
        bool changed,
        AccountStatusReason reason,
        string clientId,
        string actorReference);
}
