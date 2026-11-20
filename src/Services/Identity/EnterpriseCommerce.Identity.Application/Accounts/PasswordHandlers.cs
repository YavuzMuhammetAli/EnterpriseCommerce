using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.Accounts;

/// <summary>
/// "Forgot password" (PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.6.2, ADR-018 section 2). The caller always gets the
/// same neutral outcome. A confirmed, active account is sent a reset link; an unconfirmed, active account a
/// confirmation link instead; an unknown or disabled address nothing. The per-account resend limits apply silently.
/// </summary>
public sealed partial class RequestPasswordResetHandler(
    IAuthoritativeSecurityTransaction transaction,
    SecurityEmailScheduler scheduler,
    ILogger<RequestPasswordResetHandler> logger)
{
    public async Task HandleAsync(string? email, CancellationToken cancellationToken)
    {
        if (!AccountEmail.TryNormalize(email, out _, out var normalized))
        {
            return;
        }

        var deliveryId = await transaction.ExecuteAsync(
            "request-password-reset",
            async (unit, ct) =>
            {
                var account = await unit.FindAccountByEmailAsync(normalized, ct);
                if (account is not { State.Status: AccountStatus.Active })
                {
                    return TransactionDecision.Rollback<Guid?>(null);
                }

                var purpose = account.State.EmailConfirmed ? SecurityTokenPurpose.PasswordReset : SecurityTokenPurpose.EmailConfirmation;
                var delivery = await SecurityEmailRequests.TryRequestAsync(unit, account.UserId, purpose, ct);
                return delivery is null ? TransactionDecision.Rollback(delivery) : TransactionDecision.Commit(delivery);
            },
            cancellationToken);

        if (deliveryId is { } id)
        {
            await scheduler.ScheduleAsync(id, cancellationToken);
        }
        else
        {
            LogSuppressed(logger);
        }
    }

    [LoggerMessage(EventId = 3420, Level = LogLevel.Information, Message = "Password reset email suppressed (unknown, disabled or rate-limited account)")]
    private static partial void LogSuppressed(ILogger logger);
}

/// <summary>Outcome of a password reset or change.</summary>
public enum PasswordUpdateStatus
{
    /// <summary>The new password, the security-version increment and the token changes are committed.</summary>
    Succeeded,

    /// <summary>The new password violates the policy. Nothing was changed; a reset token stays usable.</summary>
    PasswordRejected,

    /// <summary>
    /// Reset: the token is invalid, expired, superseded or consumed, or the account is not active.
    /// Change: the password was changed concurrently, or the account is no longer active. Nothing was changed.
    /// </summary>
    Rejected,
}

/// <summary>Result of a password reset or change.</summary>
/// <param name="SecurityVersion">The committed security version. Set only on success.</param>
/// <param name="PolicyErrors">Password policy codes. Set only for <see cref="PasswordUpdateStatus.PasswordRejected"/>.</param>
public sealed record PasswordUpdateResult(PasswordUpdateStatus Status, long? SecurityVersion, IReadOnlyList<string> PolicyErrors)
{
    public static PasswordUpdateResult Succeeded(long securityVersion) => new(PasswordUpdateStatus.Succeeded, securityVersion, []);

    public static PasswordUpdateResult PasswordRejected(IReadOnlyList<string> errors) => new(PasswordUpdateStatus.PasswordRejected, null, errors);

    public static PasswordUpdateResult Rejected { get; } = new(PasswordUpdateStatus.Rejected, null, []);
}

/// <summary>
/// Password reset confirmation (ADR-017 sections 7 and 8, ADR-018 section 1). Only the POST of the reset page calls it.
/// </summary>
/// <remarks>
/// <para>
/// One authoritative transaction consumes the reset token, sets the password, raises the security version and
/// supersedes every other outstanding security token of the user. Any failure rolls all of it back, so the token
/// stays usable until it expires. A missing security-version record fails closed.
/// </para>
/// <para>
/// After commit, every authorization and token of the user is revoked as cleanup. The raised security version already
/// blocks every earlier session, so a cleanup failure changes nothing about the outcome. No session is created.
/// </para>
/// </remarks>
public sealed partial class ResetPasswordHandler(
    IAuthoritativeSecurityTransaction transaction,
    ISessionRevocationCleanup cleanup,
    IAccountPasswordPolicy passwordPolicy,
    IAccountPasswordHasher passwordHasher,
    TimeProvider timeProvider,
    ILogger<ResetPasswordHandler> logger)
{
    public const string Operation = "password-reset";

    public async Task<PasswordUpdateResult> HandleAsync(string? rawToken, string? newPassword, CancellationToken cancellationToken)
    {
        if (!SecurityTokenCodec.HasValidFormat(rawToken))
        {
            IdentityMetrics.SecurityTokenConsumption.Add(
                1,
                IdentityMetrics.Tag("purpose", nameof(SecurityTokenPurpose.PasswordReset)),
                IdentityMetrics.Tag("outcome", "rejected"));
            LogRejected(logger);
            return PasswordUpdateResult.Rejected;
        }

        var errors = await passwordPolicy.ValidateAsync(newPassword ?? string.Empty, cancellationToken);
        if (errors.Count > 0)
        {
            return PasswordUpdateResult.PasswordRejected(errors);
        }

        // Before the transaction: the slow hash never runs while a lock is held.
        var newHash = passwordHasher.Hash(newPassword!);
        var tokenHash = SecurityTokenCodec.Hash(rawToken!);
        var now = timeProvider.GetUtcNow();

        var (outcome, userId, version) = await transaction.ExecuteAsync(
            Operation,
            async (unit, ct) =>
            {
                // The token is looked up by its hash only. No user ID from the request selects the account.
                var owner = await unit.TryConsumeSecurityTokenAsync(tokenHash, SecurityTokenPurpose.PasswordReset, now, ct);
                if (owner is null || !await unit.TrySetPasswordHashAsync(owner, newHash, expectedCurrentHash: null, ct))
                {
                    return TransactionDecision.Rollback(new ResetAttempt(ResetOutcome.Rejected, owner, null));
                }

                var newVersion = await unit.IncrementSecurityVersionAsync(owner, ct);
                if (newVersion is null)
                {
                    return TransactionDecision.Rollback(new ResetAttempt(ResetOutcome.MissingSecurityVersion, owner, null));
                }

                await unit.SupersedeSecurityTokensAsync(owner, purpose: null, now, ct);
                return TransactionDecision.Commit(new ResetAttempt(ResetOutcome.Committed, owner, newVersion));
            },
            cancellationToken);

        IdentityMetrics.SecurityTokenConsumption.Add(
            1,
            IdentityMetrics.Tag("purpose", nameof(SecurityTokenPurpose.PasswordReset)),
            IdentityMetrics.Tag("outcome", outcome switch
            {
                ResetOutcome.Committed => "consumed",
                ResetOutcome.Rejected => "rejected",
                _ => "failed",
            }));

        switch (outcome)
        {
            case ResetOutcome.Rejected:
                LogRejected(logger);
                return PasswordUpdateResult.Rejected;

            case ResetOutcome.MissingSecurityVersion:
                LogMissingSecurityVersion(logger, userId!);
                throw new SecurityStateUnavailableException("The security-version record of the user is missing.");
        }

        var cleanupOutcome = await cleanup.RevokeAllForSubjectAsync(userId!, cancellationToken);
        LogPasswordReset(logger, userId!, version!.Value, cleanupOutcome);
        return PasswordUpdateResult.Succeeded(version.Value);
    }

    private enum ResetOutcome
    {
        Committed,
        Rejected,
        MissingSecurityVersion,
    }

    private readonly record struct ResetAttempt(ResetOutcome Outcome, string? UserId, long? Version);

    [LoggerMessage(EventId = 3421, Level = LogLevel.Information, Message = "Security event PasswordReset for subject {Subject} at security version {SecurityVersion}; cleanup {CleanupOutcome}")]
    private static partial void LogPasswordReset(ILogger logger, string subject, long securityVersion, CleanupOutcome cleanupOutcome);

    [LoggerMessage(EventId = 3422, Level = LogLevel.Warning, Message = "Security event SecurityTokenRejected: purpose PasswordReset")]
    private static partial void LogRejected(ILogger logger);

    [LoggerMessage(EventId = 3423, Level = LogLevel.Error, Message = "Password reset failed closed: no security-version record for subject {Subject}")]
    private static partial void LogMissingSecurityVersion(ILogger logger, string subject);
}

/// <summary>
/// Authenticated password change (ADR-017 section 7). The caller has already verified the current password and passes
/// the hash it verified against, plus the user ID from the authenticated principal only.
/// </summary>
/// <remarks>
/// <para>
/// One authoritative transaction sets the new hash only while the stored hash is still the verified one (so exactly
/// one of two concurrent changes wins), raises the security version and supersedes outstanding reset tokens.
/// </para>
/// <para>
/// The raised version invalidates every session, including the caller's current cookie. The caller reissues the
/// current browser cookie with the returned version, and only after this method returns. Every authorization and token
/// of the user is revoked as post-commit cleanup.
/// </para>
/// </remarks>
public sealed partial class ChangePasswordHandler(
    IAuthoritativeSecurityTransaction transaction,
    ISessionRevocationCleanup cleanup,
    IAccountPasswordPolicy passwordPolicy,
    IAccountPasswordHasher passwordHasher,
    TimeProvider timeProvider,
    ILogger<ChangePasswordHandler> logger)
{
    public const string Operation = "password-change";

    public async Task<PasswordUpdateResult> HandleAsync(string userId, string verifiedCurrentHash, string? newPassword, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedCurrentHash);

        var errors = await passwordPolicy.ValidateAsync(newPassword ?? string.Empty, cancellationToken);
        if (errors.Count > 0)
        {
            return PasswordUpdateResult.PasswordRejected(errors);
        }

        var newHash = passwordHasher.Hash(newPassword!);
        var now = timeProvider.GetUtcNow();

        var (changed, version) = await transaction.ExecuteAsync(
            Operation,
            async (unit, ct) =>
            {
                if (!await unit.TrySetPasswordHashAsync(userId, newHash, verifiedCurrentHash, ct))
                {
                    return TransactionDecision.Rollback((false, (long?)null));
                }

                var newVersion = await unit.IncrementSecurityVersionAsync(userId, ct);
                if (newVersion is null)
                {
                    return TransactionDecision.Rollback((true, newVersion));
                }

                await unit.SupersedeSecurityTokensAsync(userId, SecurityTokenPurpose.PasswordReset, now, ct);
                return TransactionDecision.Commit((true, newVersion));
            },
            cancellationToken);

        if (!changed)
        {
            LogConflict(logger, userId);
            return PasswordUpdateResult.Rejected;
        }

        if (version is null)
        {
            LogMissingSecurityVersion(logger, userId);
            throw new SecurityStateUnavailableException("The security-version record of the user is missing.");
        }

        var cleanupOutcome = await cleanup.RevokeAllForSubjectAsync(userId, cancellationToken);
        LogPasswordChanged(logger, userId, version.Value, cleanupOutcome);
        return PasswordUpdateResult.Succeeded(version.Value);
    }

    [LoggerMessage(EventId = 3430, Level = LogLevel.Information, Message = "Security event PasswordChanged for subject {Subject} at security version {SecurityVersion}; cleanup {CleanupOutcome}")]
    private static partial void LogPasswordChanged(ILogger logger, string subject, long securityVersion, CleanupOutcome cleanupOutcome);

    [LoggerMessage(EventId = 3431, Level = LogLevel.Warning, Message = "Password change for subject {Subject} rejected: the password changed concurrently or the account is not active")]
    private static partial void LogConflict(ILogger logger, string subject);

    [LoggerMessage(EventId = 3432, Level = LogLevel.Error, Message = "Password change failed closed: no security-version record for subject {Subject}")]
    private static partial void LogMissingSecurityVersion(ILogger logger, string subject);
}
