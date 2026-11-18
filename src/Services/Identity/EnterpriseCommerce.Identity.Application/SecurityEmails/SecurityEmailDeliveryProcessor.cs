using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.SecurityEmails;

/// <summary>
/// Processes one delivery reference from the queue (ADR-018 section 3):
/// claim (Queued to Dispatching), mint the token only after a successful claim, store only its hash, build the link in
/// memory, hand it over with bounded retries that reuse the same token and delivery identity, and record the outcome.
/// </summary>
public sealed partial class SecurityEmailDeliveryProcessor(
    ISecurityEmailDeliveryStore store,
    IAuthoritativeSecurityTransaction transaction,
    ISecurityEmailDispatcher dispatcher,
    SecurityEmailSettings settings,
    TimeProvider timeProvider,
    ILogger<SecurityEmailDeliveryProcessor> logger)
{
    public async Task ProcessAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        if (!await store.TryClaimAsync(deliveryId, cancellationToken))
        {
            // Already handled, dropped by reconciliation, or unknown: nothing is minted or sent.
            LogNotClaimed(logger, deliveryId);
            return;
        }

        var delivery = await store.ReadClaimedAsync(deliveryId, cancellationToken);
        if (delivery is null)
        {
            return;
        }

        if (delivery.Purpose != SecurityTokenPurpose.EmailConfirmation)
        {
            await CompleteAsync(deliveryId, SecurityEmailDeliveryStatus.Failed, "unsupported-purpose", cancellationToken);
            return;
        }

        if (delivery.Account.EmailConfirmed || delivery.Account.Status != Domain.Accounts.AccountStatus.Active)
        {
            await CompleteAsync(deliveryId, SecurityEmailDeliveryStatus.Failed, "not-applicable", cancellationToken);
            return;
        }

        var generated = SecurityTokenCodec.Generate();
        var token = AccountSecurityToken.Issue(
            delivery.UserId,
            delivery.Purpose,
            generated.TokenHash,
            timeProvider.GetUtcNow(),
            settings.ConfirmationTokenLifetime);

        // Persisting the hash supersedes the account's earlier unconsumed tokens of this purpose.
        var linked = await transaction.ExecuteAsync(
            "security-email-mint-token",
            async (unit, ct) =>
            {
                await unit.AddSecurityTokenAsync(token, ct);
                return await unit.LinkSecurityEmailTokenAsync(deliveryId, token.Id, ct)
                    ? TransactionDecision.Commit(true)
                    : TransactionDecision.Rollback(false);
            },
            cancellationToken);

        if (!linked)
        {
            // Reconciliation won while the token was being minted: nothing was stored and nothing is sent.
            LogNotClaimed(logger, deliveryId);
            return;
        }

        var message = new SecurityEmailMessage(deliveryId, delivery.Purpose, delivery.Email, BuildLink(generated.RawToken));
        await DispatchWithRetryAsync(message, cancellationToken);
    }

    private async Task DispatchWithRetryAsync(SecurityEmailMessage message, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!await store.RecordAttemptAsync(message.DeliveryId, cancellationToken))
            {
                // No longer dispatching (reconciled as indeterminate): stop without another handover.
                return;
            }

            SecurityEmailDispatchOutcome outcome;
            try
            {
                outcome = await dispatcher.DispatchAsync(message, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A dispatcher must classify failures; an escaping exception may have happened after sending.
                LogDispatcherThrew(logger, exception.GetType().Name, message.DeliveryId);
                outcome = SecurityEmailDispatchOutcome.Unknown;
            }

            var decision = SecurityEmailRetryPolicy.Decide(outcome, attempt, settings.MaxDispatchAttempts);
            if (!decision.Retry)
            {
                if (decision.ErrorCategory is { } category && category.StartsWith("retries-exhausted", StringComparison.Ordinal))
                {
                    LogRetriesExhausted(logger, message.DeliveryId, attempt, outcome);
                }

                await CompleteAsync(message.DeliveryId, decision.FinalStatus, decision.ErrorCategory, cancellationToken);
                return;
            }

            LogRetrying(logger, message.DeliveryId, attempt, outcome);
            await Task.Delay(settings.RetryBaseDelay * Math.Pow(2, attempt - 1), timeProvider, cancellationToken);
        }
    }

    private async Task CompleteAsync(Guid deliveryId, SecurityEmailDeliveryStatus status, string? errorCategory, CancellationToken cancellationToken)
    {
        var recorded = await store.CompleteAsync(deliveryId, status, errorCategory, cancellationToken);
        LogCompleted(logger, deliveryId, status, errorCategory, recorded);
    }

    private Uri BuildLink(string rawToken) =>
        new($"{settings.ConfirmationPage.AbsoluteUri}?token={Uri.EscapeDataString(rawToken)}");

    [LoggerMessage(EventId = 3310, Level = LogLevel.Debug, Message = "Security email delivery {DeliveryId} not claimed; discarded without minting a token")]
    private static partial void LogNotClaimed(ILogger logger, Guid deliveryId);

    [LoggerMessage(EventId = 3311, Level = LogLevel.Information, Message = "Security email delivery {DeliveryId} completed as {Status} ({ErrorCategory}); recorded {Recorded}")]
    private static partial void LogCompleted(ILogger logger, Guid deliveryId, SecurityEmailDeliveryStatus status, string? errorCategory, bool recorded);

    [LoggerMessage(EventId = 3312, Level = LogLevel.Warning, Message = "Security email delivery {DeliveryId} attempt {Attempt} ended {Outcome}; retrying with the same token and identity")]
    private static partial void LogRetrying(ILogger logger, Guid deliveryId, int attempt, SecurityEmailDispatchOutcome outcome);

    [LoggerMessage(EventId = 3313, Level = LogLevel.Error, Message = "Security email delivery {DeliveryId} retries exhausted after {Attempts} attempts; last outcome {Outcome}")]
    private static partial void LogRetriesExhausted(ILogger logger, Guid deliveryId, int attempts, SecurityEmailDispatchOutcome outcome);

    [LoggerMessage(EventId = 3314, Level = LogLevel.Error, Message = "Security email dispatcher threw {ExceptionType} for delivery {DeliveryId}; outcome treated as unknown")]
    private static partial void LogDispatcherThrew(ILogger logger, string exceptionType, Guid deliveryId);
}
