using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;

namespace EnterpriseCommerce.Inventory.Infrastructure.Messaging;

/// <summary>
/// The one explicit mapper from Inventory domain events to integration event contracts (ADR-023 section 5, ADR-028
/// section 2). It is the only messaging code that knows business types. Contracts: <c>docs/events/INVENTORY_EVENTS.md</c>.
/// </summary>
internal static class InventoryIntegrationEvents
{
    /// <summary>The producer name in every envelope.</summary>
    public const string Producer = "inventory";

    public const string StockLevelChangedEventType = "StockLevelChanged";
    public const string ReservationCreatedEventType = "ReservationCreated";
    public const string ReservationCommittedEventType = "ReservationCommitted";
    public const string ReservationReleasedEventType = "ReservationReleased";
    public const string ReservationExpiredEventType = "ReservationExpired";

    /// <summary>
    /// The versions each public event is published in. One version normally; a breaking change lists the new version
    /// next to the old one for the dual-publication period (docs/events/README.md, "A breaking change").
    /// </summary>
    public static IReadOnlyList<int> Versions { get; } = [1];

    /// <summary>The outbox rows of one domain event: one per published version, sharing one event ID.</summary>
    /// <exception cref="InvalidOperationException">The event has no mapping, or breaks a contract rule.</exception>
    public static IReadOnlyList<OutboxMessage> ToOutboxMessages(IDomainEvent domainEvent, MessageMetadata metadata, string topic)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return IntegrationEventSerializer.ToOutboxMessages(ToFact(domainEvent), Producer, metadata.CorrelationId, metadata.CausationId, topic);
    }

    /// <summary>
    /// Maps a domain event to its fact. Every domain event of the service is public; a new one must be listed here, so
    /// that it is never silently left unpublished.
    /// </summary>
    public static IntegrationEventFact ToFact(IDomainEvent domainEvent) => domainEvent switch
    {
        // The movement is the fact: its version 7 ID is the event ID, so one movement is one fact (ADR-028 section 2).
        StockLevelChanged changed => Fact(changed.MovementId, StockLevelChangedEventType, changed.InventoryItemId, changed.TenantId, changed.OccurredAt, _ => StockLevelChangedPayload(changed)),
        ReservationCreated created => ReservationFact(ReservationCreatedEventType, created.Reservation, created.OccurredAt),
        ReservationCommitted committed => ReservationFact(ReservationCommittedEventType, committed.Reservation, committed.OccurredAt),
        ReservationReleased released => ReservationFact(ReservationReleasedEventType, released.Reservation, released.OccurredAt),
        ReservationExpired expired => ReservationFact(ReservationExpiredEventType, expired.Reservation, expired.OccurredAt),
        null => throw new ArgumentNullException(nameof(domainEvent)),
        _ => throw new InvalidOperationException($"The domain event {domainEvent.GetType().Name} has no integration event mapping."),
    };

    private static IntegrationEventFact ReservationFact(string eventType, ReservationSnapshot reservation, DateTimeOffset occurredAt) =>
        Fact(Guid.CreateVersion7(occurredAt), eventType, reservation.ReservationId, reservation.TenantId, occurredAt, _ => ReservationPayload(reservation));

    private static IntegrationEventFact Fact(Guid eventId, string eventType, Guid aggregateId, Guid tenantId, DateTimeOffset occurredAt, Func<int, object> payload) =>
        new(eventId, eventType, aggregateId, tenantId, occurredAt, [.. Versions.Select(version => new IntegrationEventVersion(version, version switch
        {
            1 => payload(version),
            _ => throw new InvalidOperationException($"{eventType} v{version} has no payload mapping."),
        }))]);

    private static StockLevelChangedV1Payload StockLevelChangedPayload(StockLevelChanged changed) => new(
        changed.MovementId,
        changed.MovementType.ToString(),
        changed.AdjustmentReason?.ToString(),
        changed.ProductId,
        changed.VariantId,
        changed.Sku,
        changed.OnHandDelta,
        changed.ReservedDelta,
        changed.OnHand,
        changed.Reserved,
        changed.Available,
        changed.ItemVersion,
        changed.ReservationId);

    private static ReservationV1Payload ReservationPayload(ReservationSnapshot reservation) => new(
        reservation.Status.ToString(),
        [.. reservation.Lines.Select(line => new ReservationLineV1(line.InventoryItemId, line.ProductId, line.VariantId, line.Quantity))],
        reservation.ExpiresAt,
        reservation.Reference,
        reservation.Version);
}

/// <summary>
/// The payload of <c>StockLevelChanged</c> v1 (<c>docs/events/schemas/inventory/StockLevelChanged.v1.schema.json</c>).
/// The envelope's <c>aggregateId</c> is the inventory item. <paramref name="MovementType"/> and
/// <paramref name="AdjustmentReason"/> are open sets, so they are strings in the contract.
/// </summary>
internal sealed record StockLevelChangedV1Payload(
    Guid MovementId,
    string MovementType,
    string? AdjustmentReason,
    Guid ProductId,
    Guid VariantId,
    string Sku,
    int OnHandDelta,
    int ReservedDelta,
    int OnHand,
    int Reserved,
    int Available,
    long ItemVersion,
    Guid? ReservationId);

/// <summary>One line of a reservation payload.</summary>
internal sealed record ReservationLineV1(Guid InventoryItemId, Guid ProductId, Guid VariantId, int Quantity);

/// <summary>
/// The payload of <c>ReservationCreated</c>, <c>ReservationCommitted</c>, <c>ReservationReleased</c> and
/// <c>ReservationExpired</c> v1 (<c>docs/events/schemas/inventory/Reservation*.v1.schema.json</c>): one shape, four
/// facts. The envelope's <c>aggregateId</c> is the reservation. <paramref name="Status"/> is an open set.
/// </summary>
internal sealed record ReservationV1Payload(
    string Status,
    IReadOnlyList<ReservationLineV1> Lines,
    DateTimeOffset ExpiresAtUtc,
    string? Reference,
    long ReservationVersion);
