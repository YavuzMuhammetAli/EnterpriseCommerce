namespace EnterpriseCommerce.Identity.Domain.SecurityEmails;

/// <summary>The outcome of one handover attempt to the delivery adapter.</summary>
public enum SecurityEmailDispatchOutcome
{
    /// <summary>The adapter accepted the email.</summary>
    Delivered,

    /// <summary>The request was not processed (for example 5xx, 429, no connection). Safe to retry.</summary>
    TransientFailure,

    /// <summary>The request was rejected and a retry cannot succeed (for example 400, 403, not configured).</summary>
    PermanentFailure,

    /// <summary>The request may have been processed (for example a timeout after sending).</summary>
    Unknown,
}

/// <summary>What the worker does after an attempt.</summary>
/// <param name="Retry">Whether to try again, with the same token and the same delivery identity.</param>
/// <param name="FinalStatus">The final status when <paramref name="Retry"/> is <see langword="false"/>.</param>
/// <param name="ErrorCategory">A coarse, non-sensitive reason stored with a final failure.</param>
public sealed record SecurityEmailRetryDecision(bool Retry, SecurityEmailDeliveryStatus FinalStatus, string? ErrorCategory);

/// <summary>
/// Bounded retry of security email handovers (ADR-018 section 3). Retries reuse the same delivery identifier, which
/// is the idempotency key, so an attempt with an unknown outcome is retried safely. If the last attempt still has an
/// unknown outcome, the delivery becomes <see cref="SecurityEmailDeliveryStatus.Indeterminate"/>, never Failed.
/// </summary>
public static class SecurityEmailRetryPolicy
{
    public const int DefaultMaxAttempts = 3;

    public static SecurityEmailRetryDecision Decide(SecurityEmailDispatchOutcome outcome, int attempt, int maxAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        return outcome switch
        {
            SecurityEmailDispatchOutcome.Delivered => new(false, SecurityEmailDeliveryStatus.Sent, null),
            SecurityEmailDispatchOutcome.PermanentFailure => new(false, SecurityEmailDeliveryStatus.Failed, "rejected"),
            _ when attempt < maxAttempts => new(true, SecurityEmailDeliveryStatus.Dispatching, null),
            SecurityEmailDispatchOutcome.Unknown => new(false, SecurityEmailDeliveryStatus.Indeterminate, "retries-exhausted-unknown"),
            _ => new(false, SecurityEmailDeliveryStatus.Failed, "retries-exhausted"),
        };
    }
}
