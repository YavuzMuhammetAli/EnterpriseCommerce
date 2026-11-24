using System.Text.RegularExpressions;

namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>
/// Settings of the transactional outbox and its relay (the service's <see cref="OutboxRelayProfile.OutboxSection"/>, for
/// example <c>TenantMerchant:Outbox</c>; ADR-023 section 5). Validated at startup. The registration sets
/// <see cref="Topic"/> to the profile's default topic before it binds the section.
/// </summary>
public sealed partial class OutboxOptions
{
    /// <summary>The base of the exponential backoff (ADR-023 section 6: 1 second).</summary>
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(1);

    /// <summary>The topic written to each new row (ADR-023 section 2: <c>&lt;service&gt;.events</c>).</summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>The idle polling interval. A full batch starts the next one at once.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The rows read and published per batch.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>The cap of the exponential backoff after a failed publication.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Consecutive failed attempts at the head row after which the relay reports itself halted.</summary>
    public int HaltAfterAttempts { get; set; } = 10;

    /// <summary>Published rows older than this are deleted. Pending rows are never deleted.</summary>
    public TimeSpan PublishedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often the leader runs the cleanup.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The <c>outbox</c> health check reports <c>Degraded</c> beyond this age of the oldest pending row.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The validation errors; empty when the settings are usable.</summary>
    public IEnumerable<string> Validate()
    {
        if (!TopicPattern().IsMatch(Topic ?? string.Empty))
        {
            yield return "Topic must be 1 to 249 lowercase letters, digits, '.' and '-' (ADR-023 section 2).";
        }

        if (PollInterval < TimeSpan.FromMilliseconds(50) || PollInterval > TimeSpan.FromMinutes(1))
        {
            yield return "PollInterval must be 50 ms to 1 minute.";
        }

        if (BatchSize is < 1 or > 1000)
        {
            yield return "BatchSize must be 1 to 1000.";
        }

        if (MaxBackoff < BaseBackoff || MaxBackoff > TimeSpan.FromMinutes(10))
        {
            yield return "MaxBackoff must be 1 second to 10 minutes.";
        }

        if (HaltAfterAttempts is < 1 or > 1000)
        {
            yield return "HaltAfterAttempts must be 1 to 1000.";
        }

        if (PublishedRetention < TimeSpan.FromHours(1) || PublishedRetention > TimeSpan.FromDays(90))
        {
            yield return "PublishedRetention must be 1 hour to 90 days.";
        }

        if (CleanupInterval < TimeSpan.FromMinutes(1) || CleanupInterval > TimeSpan.FromDays(1))
        {
            yield return "CleanupInterval must be 1 minute to 1 day.";
        }

        if (StaleAfter < TimeSpan.FromSeconds(10) || StaleAfter > TimeSpan.FromDays(1))
        {
            yield return "StaleAfter must be 10 seconds to 1 day.";
        }
    }

    [GeneratedRegex("^[a-z0-9.-]{1,249}$", RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();
}
