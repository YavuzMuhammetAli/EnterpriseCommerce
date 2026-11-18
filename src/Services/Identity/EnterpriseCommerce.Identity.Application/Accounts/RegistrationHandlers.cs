using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.Accounts;

/// <summary>
/// Result of a registration request. Every accepted request has the same outcome whether or not the email already
/// belongs to an account, so the response never reveals account existence (ADR-017 section 9).
/// </summary>
/// <param name="Errors">Input errors (email format, password policy). They never depend on account existence.</param>
public sealed record RegistrationResult(bool Accepted, IReadOnlyList<string> Errors)
{
    public static RegistrationResult Accept() => new(true, []);

    public static RegistrationResult Reject(IReadOnlyList<string> errors) => new(false, errors);
}

/// <summary>
/// Registration (PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.1.1). Creates an identity only: an account with an
/// unconfirmed email, its security-version record and a queued confirmation delivery, all in one authoritative
/// transaction. The user is not signed in; sign-in requires a confirmed email.
/// </summary>
/// <remarks>
/// An email that is already registered creates nothing. If that account is still unconfirmed, a confirmation email is
/// requested again within the resend limits. The user-visible result is identical in every case.
/// </remarks>
public sealed partial class RegisterAccountHandler(
    IAuthoritativeSecurityTransaction transaction,
    IAccountPasswordPolicy passwordPolicy,
    SecurityEmailScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<RegisterAccountHandler> logger)
{
    public const string InvalidEmail = "InvalidEmail";

    public async Task<RegistrationResult> HandleAsync(string? email, string? password, CancellationToken cancellationToken)
    {
        // Input validation never depends on whether the account exists.
        var errors = new List<string>();
        if (!AccountEmail.TryNormalize(email, out var address, out var normalized))
        {
            errors.Add(InvalidEmail);
        }

        errors.AddRange(await passwordPolicy.ValidateAsync(password ?? string.Empty, cancellationToken));
        if (errors.Count > 0)
        {
            return RegistrationResult.Reject(errors);
        }

        var now = timeProvider.GetUtcNow();
        var (creation, deliveryId) = await transaction.ExecuteAsync(
            "register-account",
            async (unit, ct) =>
            {
                var created = await unit.CreateAccountAsync(address, password!, now, ct);
                switch (created.Status)
                {
                    case AccountCreationStatus.Created:
                        var delivery = await unit.CreateSecurityEmailDeliveryAsync(created.UserId!, SecurityTokenPurpose.EmailConfirmation, ct);
                        return TransactionDecision.Commit((created, (Guid?)delivery));

                    case AccountCreationStatus.DuplicateEmail:
                        var existing = await unit.FindAccountByEmailAsync(normalized, ct);
                        var resend = existing is { State: { EmailConfirmed: false, Status: AccountStatus.Active } }
                            ? await SecurityEmailRequests.TryRequestAsync(unit, existing.UserId, SecurityTokenPurpose.EmailConfirmation, ct)
                            : null;

                        // The failed insert is rolled back; only a requested delivery is kept.
                        return resend is null
                            ? TransactionDecision.Rollback((created, (Guid?)null))
                            : TransactionDecision.Commit((created, resend));

                    default:
                        return TransactionDecision.Rollback((created, (Guid?)null));
                }
            },
            cancellationToken);

        if (creation.Status == AccountCreationStatus.Rejected)
        {
            return RegistrationResult.Reject(["PasswordRejected"]);
        }

        if (deliveryId is { } id)
        {
            await scheduler.ScheduleAsync(id, cancellationToken);
        }

        LogRegistration(logger, creation.Status, creation.UserId);
        return RegistrationResult.Accept();
    }

    [LoggerMessage(EventId = 3400, Level = LogLevel.Information, Message = "Security event Registration: outcome {Outcome}, subject {Subject}")]
    private static partial void LogRegistration(ILogger logger, AccountCreationStatus outcome, string? subject);
}

/// <summary>
/// "Resend confirmation email". Always the same neutral result. A confirmation email is requested only for an
/// existing, active, unconfirmed account, and only within the resend limits.
/// </summary>
public sealed partial class ResendConfirmationHandler(
    IAuthoritativeSecurityTransaction transaction,
    SecurityEmailScheduler scheduler,
    ILogger<ResendConfirmationHandler> logger)
{
    public async Task HandleAsync(string? email, CancellationToken cancellationToken)
    {
        if (!AccountEmail.TryNormalize(email, out _, out var normalized))
        {
            return;
        }

        var deliveryId = await transaction.ExecuteAsync(
            "resend-confirmation",
            async (unit, ct) =>
            {
                var account = await unit.FindAccountByEmailAsync(normalized, ct);
                if (account is not { State: { EmailConfirmed: false, Status: AccountStatus.Active } })
                {
                    return TransactionDecision.Rollback<Guid?>(null);
                }

                var delivery = await SecurityEmailRequests.TryRequestAsync(unit, account.UserId, SecurityTokenPurpose.EmailConfirmation, ct);
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

    [LoggerMessage(EventId = 3401, Level = LogLevel.Information, Message = "Confirmation resend suppressed (unknown, confirmed or rate-limited account)")]
    private static partial void LogSuppressed(ILogger logger);
}

/// <summary>Result of a confirmation attempt. Every failure is the same generic result.</summary>
public enum EmailConfirmationResult
{
    Confirmed,
    InvalidOrExpired,
}

/// <summary>
/// Confirms an email with a single-use token (ADR-018 section 1). Only the POST of the confirmation page calls this.
/// Token consumption and the account change commit in one authoritative transaction: a failed account update rolls
/// the consumption back, and of concurrent requests exactly one can win.
/// </summary>
public sealed partial class ConfirmEmailHandler(
    IAuthoritativeSecurityTransaction transaction,
    TimeProvider timeProvider,
    ILogger<ConfirmEmailHandler> logger)
{
    public async Task<EmailConfirmationResult> HandleAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (!SecurityTokenCodec.HasValidFormat(rawToken))
        {
            return EmailConfirmationResult.InvalidOrExpired;
        }

        // The token is looked up by its hash only. No user ID from the request selects the account.
        var tokenHash = SecurityTokenCodec.Hash(rawToken!);
        var now = timeProvider.GetUtcNow();

        var userId = await transaction.ExecuteAsync(
            "confirm-email",
            async (unit, ct) =>
            {
                var owner = await unit.TryConsumeSecurityTokenAsync(tokenHash, SecurityTokenPurpose.EmailConfirmation, now, ct);
                if (owner is null || !await unit.ConfirmEmailAsync(owner, ct))
                {
                    return TransactionDecision.Rollback<string?>(null);
                }

                await unit.SupersedeSecurityTokensAsync(owner, SecurityTokenPurpose.EmailConfirmation, now, ct);
                return TransactionDecision.Commit<string?>(owner);
            },
            cancellationToken);

        if (userId is null)
        {
            LogRejected(logger);
            return EmailConfirmationResult.InvalidOrExpired;
        }

        LogConfirmed(logger, userId);
        return EmailConfirmationResult.Confirmed;
    }

    [LoggerMessage(EventId = 3410, Level = LogLevel.Information, Message = "Security event EmailConfirmed for subject {Subject}")]
    private static partial void LogConfirmed(ILogger logger, string subject);

    [LoggerMessage(EventId = 3411, Level = LogLevel.Warning, Message = "Security event SecurityTokenRejected: purpose EmailConfirmation")]
    private static partial void LogRejected(ILogger logger);
}
