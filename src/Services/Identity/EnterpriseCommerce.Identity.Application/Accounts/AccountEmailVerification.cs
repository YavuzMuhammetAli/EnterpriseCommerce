using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Accounts;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.Accounts;

/// <summary>The account facts that decide whether an email address is the verified address of an account.</summary>
/// <param name="Status">The account status.</param>
/// <param name="EmailConfirmed">Whether the account's email address is confirmed.</param>
/// <param name="NormalizedEmail">The normalized email address of the account, if any.</param>
public sealed record AccountEmailState(AccountStatus Status, bool EmailConfirmed, string? NormalizedEmail);

/// <summary>The answer to "is this the verified email address of this active account?" (ADR-021).</summary>
public enum AccountEmailVerificationResult
{
    /// <summary>The account is active, its email is confirmed, and it equals the given address.</summary>
    Match,

    /// <summary>The account is active and confirmed, but its email is a different address.</summary>
    Mismatch,

    /// <summary>The account is active, but its email address is not confirmed.</summary>
    Unverified,

    /// <summary>The account is not active (for example disabled).</summary>
    Inactive,

    /// <summary>No account has this ID.</summary>
    NotFound,
}

/// <summary>
/// Answers, for a trusted service client, whether an email address is the verified address of an active account
/// (ADR-021, the internal verification API of IDENTITY_API_CONTRACT.md section 2.13). Tenant &amp; Merchant uses it to
/// accept a merchant invitation only for the invited address (decision D14).
/// </summary>
/// <remarks>
/// The caller sends the address it knows; Identity never returns an account's email address. The read is a single
/// projection with no change, run through the authoritative transaction so that a database failure surfaces as
/// <see cref="SecurityStateUnavailableException"/> (503) instead of an answer.
/// </remarks>
public sealed partial class VerifyAccountEmailHandler(
    IAuthoritativeSecurityTransaction transaction,
    ILogger<VerifyAccountEmailHandler> logger)
{
    public const string Operation = "account-email-verification";

    /// <param name="accountId">The account (user ID, <c>sub</c>).</param>
    /// <param name="normalizedEmail">The address to compare, normalized with <see cref="AccountEmail.TryNormalize"/>.</param>
    /// <param name="callerClientId">The authenticated service client, for the security log.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<AccountEmailVerificationResult> HandleAsync(
        string accountId,
        string normalizedEmail,
        string callerClientId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEmail);

        var state = await transaction.ExecuteAsync(
            Operation,
            async (unit, ct) => TransactionDecision.Rollback(await unit.ReadAccountEmailStateAsync(accountId, ct)),
            cancellationToken);

        var result = Evaluate(state, normalizedEmail);
        LogVerified(logger, accountId, result, callerClientId);
        return result;
    }

    /// <summary>
    /// The decision, in a fixed order: unknown, then inactive, then unconfirmed, then the address comparison. Both
    /// addresses are normalized the same way (<see cref="AccountEmail.TryNormalize"/>), so the comparison is ordinal.
    /// </summary>
    public static AccountEmailVerificationResult Evaluate(AccountEmailState? state, string normalizedEmail) => state switch
    {
        null => AccountEmailVerificationResult.NotFound,
        { Status: not AccountStatus.Active } => AccountEmailVerificationResult.Inactive,
        { EmailConfirmed: false } => AccountEmailVerificationResult.Unverified,
        { NormalizedEmail: { } stored } when string.Equals(stored, normalizedEmail, StringComparison.Ordinal) => AccountEmailVerificationResult.Match,
        _ => AccountEmailVerificationResult.Mismatch,
    };

    [LoggerMessage(
        EventId = 3510,
        Level = LogLevel.Information,
        Message = "Account email verification for subject {Subject} answered {Result} to client {ClientId}")]
    private static partial void LogVerified(ILogger logger, string subject, AccountEmailVerificationResult result, string clientId);
}
