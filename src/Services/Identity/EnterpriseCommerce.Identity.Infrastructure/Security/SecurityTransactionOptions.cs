namespace EnterpriseCommerce.Identity.Infrastructure.Security;

/// <summary>Retry policy for authoritative transactions and post-commit cleanup (ADR-017 section 8).</summary>
internal sealed class SecurityTransactionOptions
{
    public const string SectionName = "Identity:SecurityTransactions";

    /// <summary>Total attempts of an authoritative transaction, including the first. Deadlocks only.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Total attempts of a post-commit cleanup step, including the first. Deadlocks only.</summary>
    public int CleanupMaxAttempts { get; set; } = 4;

    /// <summary>Base delay of the exponential backoff with jitter.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    internal TimeSpan DelayBeforeAttempt(int nextAttempt)
    {
        // nextAttempt >= 2: base * 2^(n-2), plus up to one base delay of jitter to break retry lockstep.
        var exponential = BaseDelay * Math.Pow(2, nextAttempt - 2);
        var jitter = BaseDelay * Random.Shared.NextDouble();
        return exponential + jitter;
    }
}
