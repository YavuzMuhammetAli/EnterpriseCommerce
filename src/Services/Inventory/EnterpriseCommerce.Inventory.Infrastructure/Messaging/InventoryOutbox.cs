using EnterpriseCommerce.BuildingBlocks.Messaging;

namespace EnterpriseCommerce.Inventory.Infrastructure.Messaging;

/// <summary>Inventory's use of the shared outbox and relay (ADR-028 sections 1 and 4).</summary>
internal static class InventoryOutbox
{
    public const string OutboxSection = "Inventory:Outbox";
    public const string KafkaSection = "Inventory:Kafka";
    public const string DefaultTopic = "inventory.events";
    public const string MeterName = "EnterpriseCommerce.Inventory.Outbox";

    public static OutboxRelayProfile Profile { get; } = new()
    {
        OutboxSection = OutboxSection,
        KafkaSection = KafkaSection,
        DefaultTopic = DefaultTopic,
        KafkaClientId = "inventory-outbox-relay",
        LockApplicationName = "inventory-outbox-relay",
        MeterName = MeterName,
        SettingsReference = "INVENTORY_OPERATIONS.md section 2",
    };

    /// <summary>The default settings of the service's outbox: the block's defaults with the service's topic.</summary>
    public static OutboxOptions DefaultOptions() => new() { Topic = DefaultTopic };
}
