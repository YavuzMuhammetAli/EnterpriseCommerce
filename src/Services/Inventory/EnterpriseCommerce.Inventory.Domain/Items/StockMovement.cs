using EnterpriseCommerce.Inventory.Domain.Common;

namespace EnterpriseCommerce.Inventory.Domain.Items;

/// <summary>What a movement did to an item's balance (ADR-026 section 5). The stored and published names are these.</summary>
public enum MovementType
{
    /// <summary>The item was created with its initial on-hand stock.</summary>
    InitialStock,

    /// <summary>A merchant adjustment with a reason.</summary>
    Adjustment,

    /// <summary>A reservation line took available stock: reserved increases.</summary>
    Reservation,

    /// <summary>An explicit release returned a line: reserved decreases.</summary>
    ReservationRelease,

    /// <summary>An expiry returned a line: reserved decreases.</summary>
    ReservationExpiry,

    /// <summary>A commit consumed a line: on-hand and reserved both decrease.</summary>
    ReservationCommit,
}

/// <summary>
/// The reason of a merchant adjustment, with its sign rule (ADR-026 section 4). A stock count is recorded as the
/// counted difference, never as an absolute value.
/// </summary>
public enum AdjustmentReason
{
    /// <summary>Goods received: positive.</summary>
    Receipt,

    /// <summary>A customer return put back into stock: positive.</summary>
    Return,

    /// <summary>Damaged stock: negative.</summary>
    Damage,

    /// <summary>Shrinkage or theft: negative.</summary>
    Loss,

    /// <summary>An earlier error fixed: either sign.</summary>
    Correction,

    /// <summary>A counted difference: either sign.</summary>
    CycleCount,
}

/// <summary>The sign rules of the adjustment reasons.</summary>
public static class AdjustmentReasons
{
    public static bool AllowsChange(AdjustmentReason reason, int delta) => reason switch
    {
        AdjustmentReason.Receipt or AdjustmentReason.Return => delta > 0,
        AdjustmentReason.Damage or AdjustmentReason.Loss => delta < 0,
        AdjustmentReason.Correction or AdjustmentReason.CycleCount => delta != 0,
        _ => false,
    };
}

/// <summary>
/// The key and canonical request hash that make a merchant operation idempotent (ADR-026 section 8). Stored with the row
/// the operation created; its scope is (tenant, subject, key).
/// </summary>
public sealed record IdempotencyRecord(string Key, byte[] RequestHash)
{
    public const int KeyMaxLength = 100;
    public const int HashLength = 32;

    public string Key { get; } = Key is { Length: >= 1 and <= KeyMaxLength } ? Key : throw new ArgumentException("An idempotency key has 1 to 100 characters.", nameof(Key));

    public byte[] RequestHash { get; } = RequestHash is { Length: HashLength } ? RequestHash : throw new ArgumentException("A request hash has 32 bytes.", nameof(RequestHash));
}

/// <summary>
/// One immutable entry of an item's ledger (ADR-026 section 5): exactly one per balance change, written in the change's
/// transaction. Its item version is unique per item and <c>after = before + delta</c> holds for both quantities, so
/// replaying an item's movements in version order reproduces its balance. A trigger refuses updates and deletes.
/// </summary>
public sealed class StockMovement
{
    private StockMovement()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid InventoryItemId { get; private set; }

    /// <summary>The item version this movement produced.</summary>
    public long ItemVersion { get; private set; }

    public MovementType Type { get; private set; }

    /// <summary>The adjustment reason; only for <see cref="MovementType.Adjustment"/>.</summary>
    public AdjustmentReason? Reason { get; private set; }

    public int OnHandDelta { get; private set; }

    public int ReservedDelta { get; private set; }

    public int OnHandBefore { get; private set; }

    public int ReservedBefore { get; private set; }

    public int OnHandAfter { get; private set; }

    public int ReservedAfter { get; private set; }

    public int AvailableAfter => OnHandAfter - ReservedAfter;

    /// <summary>The reservation of a reservation movement.</summary>
    public Guid? ReservationId { get; private set; }

    public ActorKind ActorKind { get; private set; }

    public string ActorId { get; private set; } = string.Empty;

    public string CorrelationId { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>The idempotency key of an adjustment; <c>null</c> for every other movement.</summary>
    public string? IdempotencyKey { get; private set; }

    public byte[]? RequestHash { get; private set; }

    internal static StockMovement Record(
        InventoryItem item,
        MovementType type,
        AdjustmentReason? reason,
        int onHandDelta,
        int reservedDelta,
        Guid? reservationId,
        Actor actor,
        string correlationId,
        DateTimeOffset occurredAt,
        IdempotencyRecord? idempotency) => new()
        {
            Id = Guid.CreateVersion7(occurredAt),
            TenantId = item.TenantId,
            InventoryItemId = item.Id,
            ItemVersion = item.Version,
            Type = type,
            Reason = reason,
            OnHandDelta = onHandDelta,
            ReservedDelta = reservedDelta,
            OnHandAfter = item.OnHand,
            ReservedAfter = item.Reserved,
            OnHandBefore = item.OnHand - onHandDelta,
            ReservedBefore = item.Reserved - reservedDelta,
            ReservationId = reservationId,
            ActorKind = actor.Kind,
            ActorId = actor.Id,
            CorrelationId = correlationId,
            OccurredAt = occurredAt,
            IdempotencyKey = idempotency?.Key,
            RequestHash = idempotency?.RequestHash,
        };
}
