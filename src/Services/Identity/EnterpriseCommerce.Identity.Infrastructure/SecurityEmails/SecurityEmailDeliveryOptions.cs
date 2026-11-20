namespace EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;

/// <summary>Security email delivery settings (<c>Identity:SecurityEmail</c>, ADR-018 section 3).</summary>
internal sealed class SecurityEmailDeliveryOptions
{
    public const string SectionName = "Identity:SecurityEmail";

    /// <summary>Capacity of the in-process wake-up channel. A full channel drops the delivery (recorded).</summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>Concurrent handovers of the single hosted worker.</summary>
    public int MaxConcurrentDispatches { get; set; } = 4;

    public int MaxDispatchAttempts { get; set; } = 3;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ConfirmationTokenLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>ADR-018 section 1: 30 minutes. At most one hour.</summary>
    public TimeSpan PasswordResetTokenLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Reconciliation runs at startup and then at this interval.</summary>
    public TimeSpan ReconcileInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Queued or Dispatching records older than this are reconciled (status only).</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The path of the Identity-hosted confirmation page, relative to the issuer.</summary>
    public string ConfirmationPath { get; set; } = "account/confirm-email";

    /// <summary>The path of the Identity-hosted password reset page, relative to the issuer.</summary>
    public string PasswordResetPath { get; set; } = "account/reset-password";

    public bool IsValid() =>
        QueueCapacity is >= 1 and <= 100_000
        && MaxConcurrentDispatches is >= 1 and <= 64
        && MaxDispatchAttempts is >= 1 and <= 10
        && RetryBaseDelay >= TimeSpan.Zero && RetryBaseDelay <= TimeSpan.FromMinutes(1)
        && ConfirmationTokenLifetime > TimeSpan.Zero && ConfirmationTokenLifetime <= TimeSpan.FromDays(7)
        && PasswordResetTokenLifetime > TimeSpan.Zero && PasswordResetTokenLifetime <= TimeSpan.FromHours(1)
        && ReconcileInterval > TimeSpan.Zero
        && StaleAfter >= TimeSpan.FromMinutes(1)
        && !string.IsNullOrWhiteSpace(ConfirmationPath)
        && !string.IsNullOrWhiteSpace(PasswordResetPath);
}
