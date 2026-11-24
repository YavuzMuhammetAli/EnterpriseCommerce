using EnterpriseCommerce.BuildingBlocks.Messaging;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

/// <summary>
/// Tenant &amp; Merchant's use of the shared outbox and relay (ADR-028 section 4). The values are those of the Phase 3 S4
/// implementation, unchanged: the settings sections, topic, producer client ID, lock session name and meter name
/// (PHASE_3_S4_EVENT_FLOW.md sections 5 and 7, TENANT_MERCHANT_EVENT_RELAY.md).
/// </summary>
internal static class TenantMerchantOutbox
{
    public const string OutboxSection = "TenantMerchant:Outbox";
    public const string KafkaSection = "TenantMerchant:Kafka";
    public const string DefaultTopic = "tenant-merchant.events";
    public const string MeterName = "EnterpriseCommerce.TenantMerchant.Outbox";

    public static OutboxRelayProfile Profile { get; } = new()
    {
        OutboxSection = OutboxSection,
        KafkaSection = KafkaSection,
        DefaultTopic = DefaultTopic,
        KafkaClientId = "tenant-merchant-outbox-relay",
        LockApplicationName = "tenant-merchant-outbox-relay",
        MeterName = MeterName,
        SettingsReference = "PHASE_3_S4_EVENT_FLOW.md section 5",
    };

    /// <summary>The default settings of the service's outbox: the block's defaults with the service's topic.</summary>
    public static OutboxOptions DefaultOptions() => new() { Topic = DefaultTopic };
}
