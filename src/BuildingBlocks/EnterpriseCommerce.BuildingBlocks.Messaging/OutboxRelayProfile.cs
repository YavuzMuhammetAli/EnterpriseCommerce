using System.Text.RegularExpressions;

namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>
/// What makes the shared outbox and relay one service's (ADR-028 section 4): its settings sections, its default topic,
/// and the names that identify its relay to Kafka, to SQL Server and to monitoring. Every value is fixed in code by the
/// service, never configuration.
/// </summary>
public sealed partial record OutboxRelayProfile
{
    /// <summary>The settings section of <see cref="OutboxOptions"/>, for example <c>TenantMerchant:Outbox</c>.</summary>
    public required string OutboxSection { get; init; }

    /// <summary>The settings section of <see cref="KafkaOptions"/>, for example <c>TenantMerchant:Kafka</c>.</summary>
    public required string KafkaSection { get; init; }

    /// <summary>The service's topic (ADR-023 section 2: <c>&lt;service&gt;.events</c>), unless the outbox section overrides it.</summary>
    public required string DefaultTopic { get; init; }

    /// <summary>The producer's <c>client.id</c>, for example <c>tenant-merchant-outbox-relay</c>.</summary>
    public required string KafkaClientId { get; init; }

    /// <summary>The application name of the leadership session, visible in <c>sys.dm_exec_sessions</c>.</summary>
    public required string LockApplicationName { get; init; }

    /// <summary>The meter name of the relay, for example <c>EnterpriseCommerce.TenantMerchant.Outbox</c>.</summary>
    public required string MeterName { get; init; }

    /// <summary>The document that lists the allowed settings, named in startup validation errors.</summary>
    public required string SettingsReference { get; init; }

    /// <summary>The validation errors of a profile; a service with an invalid profile is a programming error.</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(OutboxSection) || string.IsNullOrWhiteSpace(KafkaSection) || OutboxSection == KafkaSection)
        {
            yield return "OutboxSection and KafkaSection must be two different, non-empty settings sections.";
        }

        if (!TopicPattern().IsMatch(DefaultTopic ?? string.Empty))
        {
            yield return "DefaultTopic must be 1 to 249 lowercase letters, digits, '.' and '-' (ADR-023 section 2).";
        }

        if (!NamePattern().IsMatch(KafkaClientId ?? string.Empty) || !NamePattern().IsMatch(LockApplicationName ?? string.Empty))
        {
            yield return "KafkaClientId and LockApplicationName must be 1 to 128 lowercase letters, digits and '-'.";
        }

        if (string.IsNullOrWhiteSpace(MeterName) || string.IsNullOrWhiteSpace(SettingsReference))
        {
            yield return "MeterName and SettingsReference are required.";
        }
    }

    [GeneratedRegex("^[a-z0-9.-]{1,249}$", RegexOptions.CultureInvariant)]
    private static partial Regex TopicPattern();

    [GeneratedRegex("^[a-z0-9-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
