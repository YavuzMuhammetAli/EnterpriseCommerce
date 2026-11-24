namespace EnterpriseCommerce.Inventory.Domain.Reservations;

/// <summary>One line of a reservation event: the item, its Catalog identity and the quantity.</summary>
public sealed record ReservationEventLine(Guid InventoryItemId, Guid ProductId, Guid VariantId, int Quantity);

/// <summary>The state of a reservation that its events publish (ADR-028 section 2). It names no client and no actor.</summary>
public sealed record ReservationSnapshot(
    Guid ReservationId,
    Guid TenantId,
    ReservationStatus Status,
    IReadOnlyList<ReservationEventLine> Lines,
    DateTimeOffset ExpiresAt,
    string? Reference,
    long Version)
{
    internal static ReservationSnapshot Of(Reservation reservation) => new(
        reservation.Id,
        reservation.TenantId,
        reservation.Status,
        // Ascending item ID as .NET orders GUIDs (the lock order), whatever order the lines were loaded in.
        [.. reservation.Lines.OrderBy(line => line.InventoryItemId).Select(line => new ReservationEventLine(line.InventoryItemId, line.ProductId, line.VariantId, line.Quantity))],
        reservation.ExpiresAt,
        reservation.Reference,
        reservation.Version);
}

/// <summary>A reservation was placed. Published as <c>ReservationCreated</c> v1.</summary>
public sealed record ReservationCreated(ReservationSnapshot Reservation, DateTimeOffset OccurredAt) : Common.IDomainEvent
{
    internal ReservationCreated(Reservation reservation)
        : this(ReservationSnapshot.Of(reservation), reservation.CreatedAt)
    {
    }
}

/// <summary>An active reservation was committed. Published as <c>ReservationCommitted</c> v1.</summary>
public sealed record ReservationCommitted(ReservationSnapshot Reservation, DateTimeOffset OccurredAt) : Common.IDomainEvent
{
    internal ReservationCommitted(Reservation reservation)
        : this(ReservationSnapshot.Of(reservation), reservation.CompletedAt!.Value)
    {
    }
}

/// <summary>An active reservation was released explicitly. Published as <c>ReservationReleased</c> v1.</summary>
public sealed record ReservationReleased(ReservationSnapshot Reservation, DateTimeOffset OccurredAt) : Common.IDomainEvent
{
    internal ReservationReleased(Reservation reservation)
        : this(ReservationSnapshot.Of(reservation), reservation.CompletedAt!.Value)
    {
    }
}

/// <summary>An overdue active reservation expired, by the worker or lazily. Published as <c>ReservationExpired</c> v1.</summary>
public sealed record ReservationExpired(ReservationSnapshot Reservation, DateTimeOffset OccurredAt) : Common.IDomainEvent
{
    internal ReservationExpired(Reservation reservation)
        : this(ReservationSnapshot.Of(reservation), reservation.CompletedAt!.Value)
    {
    }
}
