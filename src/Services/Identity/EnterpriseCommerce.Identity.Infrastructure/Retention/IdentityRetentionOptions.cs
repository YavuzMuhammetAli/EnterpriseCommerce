using EnterpriseCommerce.Identity.Domain.Security;

namespace EnterpriseCommerce.Identity.Infrastructure.Retention;

/// <summary>
/// Retention cleanup of expired replay-protection records (configuration section <c>Identity:Retention</c>).
/// See docs/security/IDENTITY_OPERATIONS.md for the policy of every record category.
/// </summary>
public sealed class IdentityRetentionOptions
{
    public const string SectionName = "Identity:Retention";

    /// <summary>Whether the hosted cleanup worker runs. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Delay after startup before the first run, so that cleanup never competes with startup.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Time between two runs of one instance.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Rows per delete statement. At most 4000, below SQL Server's lock escalation threshold.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>Delete statements per category and run. The rest waits for the next run.</summary>
    public int MaxBatchesPerRun { get; set; } = 100;

    /// <summary>The longest a ledger delete waits for a row lock before it fails as transient (error 1222).</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Attempts per statement for transient failures (deadlock, lock or command timeout).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Added to the longest token lifetime. At least <see cref="ReplayProtectionWindow.MinimumSafetyMargin"/>.</summary>
    public TimeSpan SafetyMargin { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Optional operator floor for the replay protection window, for example the previous, longer lifetime after a
    /// lifetime was reduced. It never shortens the window computed from the configured lifetimes.
    /// </summary>
    public TimeSpan? MinimumReplayProtectionWindow { get; set; }

    /// <summary>
    /// The effective window, computed by the host from the configured client lifetimes
    /// (<see cref="RetentionServiceCollectionExtensions.UseReplayProtectionLifetimes"/>). Never bound from configuration.
    /// </summary>
    public TimeSpan ReplayProtectionWindow { get; internal set; }

    internal bool IsValid() =>
        InitialDelay >= TimeSpan.Zero
        && Interval >= TimeSpan.FromMinutes(1)
        && BatchSize is >= 1 and <= 4000
        && MaxBatchesPerRun is >= 1 and <= 10_000
        && LockTimeout >= TimeSpan.FromMilliseconds(100) && LockTimeout <= TimeSpan.FromMinutes(1)
        && MaxAttempts is >= 1 and <= 10
        && SafetyMargin >= Domain.Security.ReplayProtectionWindow.MinimumSafetyMargin
        && ReplayProtectionWindow > TimeSpan.Zero;
}
