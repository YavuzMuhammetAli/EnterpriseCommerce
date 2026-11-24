using EnterpriseCommerce.Inventory.Domain.Common;

namespace EnterpriseCommerce.Inventory.Domain.Items;

/// <summary>Why a stock change was refused. Nothing changes when one is returned.</summary>
public enum StockChangeError
{
    None,

    /// <summary>The quantity is outside the bounds of ADR-026 section 1.</summary>
    QuantityOutOfRange,

    /// <summary>The adjustment's sign contradicts its reason (ADR-026 section 4).</summary>
    ReasonDoesNotAllowChange,

    /// <summary>The adjustment would leave on-hand stock below the reserved quantity, or below zero.</summary>
    InsufficientUnreservedStock,

    /// <summary>The adjustment would exceed the on-hand maximum.</summary>
    AboveMaximum,

    /// <summary>The reservation line needs more than the available stock.</summary>
    InsufficientAvailableStock,
}

/// <summary>The outcome of a stock change: the movement it recorded, or why it was refused.</summary>
public readonly record struct StockChange(StockMovement? Movement, StockChangeError Error)
{
    public bool Succeeded => Error == StockChangeError.None;

    internal static StockChange Refused(StockChangeError error) => new(null, error);
}

/// <summary>
/// The stock balance of one Catalog variant in one tenant (ADR-026 sections 2 and 3): the aggregate that every stock
/// change goes through. <c>Available = OnHand − Reserved</c> is derived; <c>0 ≤ Reserved ≤ OnHand ≤ max</c> holds after
/// every method, and the database enforces it again.
/// </summary>
/// <remarks>
/// Every change increments <see cref="Version"/>, records exactly one <see cref="StockMovement"/> in
/// <see cref="PendingMovements"/> and one <see cref="StockLevelChanged"/> event. The persistence adds the pending
/// movements and the events' outbox rows in the same transaction, so a balance never changes without its ledger entry.
/// Callers lock the item's row before calling a method (ADR-026 section 9); the methods never read another source.
/// </remarks>
public sealed class InventoryItem : IHasDomainEvents
{
    private readonly List<IDomainEvent> _domainEvents = [];
    private readonly List<StockMovement> _pendingMovements = [];

    private InventoryItem()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>Catalog's product ID, verified at creation (ADR-027). No foreign key: another service owns it.</summary>
    public Guid ProductId { get; private set; }

    /// <summary>Catalog's variant ID: unique per tenant (ADR-026 section 2).</summary>
    public Guid VariantId { get; private set; }

    /// <summary>The SKU snapshot from the creation-time verification.</summary>
    public string Sku { get; private set; } = string.Empty;

    public int OnHand { get; private set; }

    public int Reserved { get; private set; }

    public int Available => OnHand - Reserved;

    /// <summary>Incremented by every movement; the movement records it, and events publish it (ADR-028 section 3).</summary>
    public long Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The creating merchant subject: with <see cref="CreationKey"/>, the idempotency scope of the creation.</summary>
    public string CreatedBySubject { get; private set; } = string.Empty;

    public string CreationKey { get; private set; } = string.Empty;

    public byte[] CreationRequestHash { get; private set; } = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    /// <summary>The movements recorded since the item was loaded or last saved.</summary>
    public IReadOnlyList<StockMovement> PendingMovements => _pendingMovements;

    /// <summary>
    /// Creates the item of a verified variant with its initial stock, recorded as an <see cref="MovementType.InitialStock"/>
    /// movement (version 1).
    /// </summary>
    public static (InventoryItem? Item, StockChangeError Error) Create(
        Guid tenantId,
        Guid productId,
        Guid variantId,
        SkuSnapshot sku,
        int initialOnHand,
        string subjectId,
        IdempotencyRecord creation,
        string correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(sku);
        ArgumentNullException.ThrowIfNull(creation);
        if (tenantId == Guid.Empty || productId == Guid.Empty || variantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant, product and variant IDs are required.");
        }

        if (!Quantities.IsValidInitialOnHand(initialOnHand))
        {
            return (null, StockChangeError.QuantityOutOfRange);
        }

        var actor = Actor.User(subjectId);
        var item = new InventoryItem
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            ProductId = productId,
            VariantId = variantId,
            Sku = sku.Value,
            CreatedAt = now,
            CreatedBySubject = actor.Id,
            CreationKey = creation.Key,
            CreationRequestHash = creation.RequestHash,
        };
        item.Apply(MovementType.InitialStock, null, initialOnHand, 0, null, actor, correlationId, now, null);
        return (item, StockChangeError.None);
    }

    /// <summary>A merchant adjustment: an explicit, non-zero change of on-hand stock with a reason (ADR-026 section 4).</summary>
    public StockChange Adjust(int delta, AdjustmentReason reason, Actor actor, IdempotencyRecord idempotency, string correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(idempotency);
        if (!Quantities.IsValidAdjustment(delta))
        {
            return StockChange.Refused(StockChangeError.QuantityOutOfRange);
        }

        if (!Enum.IsDefined(reason) || !AdjustmentReasons.AllowsChange(reason, delta))
        {
            return StockChange.Refused(StockChangeError.ReasonDoesNotAllowChange);
        }

        // long arithmetic: the bounds are checked before the int balance changes.
        var onHand = (long)OnHand + delta;
        if (onHand < Reserved)
        {
            return StockChange.Refused(StockChangeError.InsufficientUnreservedStock);
        }

        if (onHand > Quantities.MaxOnHand)
        {
            return StockChange.Refused(StockChangeError.AboveMaximum);
        }

        return new StockChange(Apply(MovementType.Adjustment, reason, delta, 0, null, actor, correlationId, now, idempotency), StockChangeError.None);
    }

    /// <summary>Whether a reservation line of <paramref name="quantity"/> fits the available stock.</summary>
    public bool CanReserve(int quantity) => Quantities.IsValidLineQuantity(quantity) && quantity <= Available;

    /// <summary>Takes available stock for one reservation line: reserved increases, on-hand is unchanged.</summary>
    internal StockChange Reserve(int quantity, Guid reservationId, Actor actor, string correlationId, DateTimeOffset now)
    {
        if (!Quantities.IsValidLineQuantity(quantity))
        {
            return StockChange.Refused(StockChangeError.QuantityOutOfRange);
        }

        if (quantity > Available)
        {
            return StockChange.Refused(StockChangeError.InsufficientAvailableStock);
        }

        return new StockChange(Apply(MovementType.Reservation, null, 0, quantity, reservationId, actor, correlationId, now, null), StockChangeError.None);
    }

    /// <summary>Returns one line of a released or expired reservation: reserved decreases, on-hand is unchanged.</summary>
    internal StockMovement ReturnReserved(int quantity, MovementType type, Guid reservationId, Actor actor, string correlationId, DateTimeOffset now)
    {
        if (type is not (MovementType.ReservationRelease or MovementType.ReservationExpiry))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        EnsureReserved(quantity);
        return Apply(type, null, 0, -quantity, reservationId, actor, correlationId, now, null);
    }

    /// <summary>Consumes one line of a committed reservation: on-hand and reserved both decrease, availability is unchanged.</summary>
    internal StockMovement CommitReserved(int quantity, Guid reservationId, Actor actor, string correlationId, DateTimeOffset now)
    {
        EnsureReserved(quantity);
        return Apply(MovementType.ReservationCommit, null, -quantity, -quantity, reservationId, actor, correlationId, now, null);
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <summary>Forgets the pending movements once the save that persists them has taken them.</summary>
    public void ClearPendingMovements() => _pendingMovements.Clear();

    /// <summary>
    /// A reservation's quantity is always still reserved on its item: only that reservation's own transitions return
    /// it, and an adjustment never goes below the reserved quantity. Anything else is a broken invariant.
    /// </summary>
    private void EnsureReserved(int quantity)
    {
        if (!Quantities.IsValidLineQuantity(quantity) || quantity > Reserved || quantity > OnHand)
        {
            throw new InvalidOperationException("A reservation line exceeds the item's reserved stock: the inventory invariant is broken.");
        }
    }

    private StockMovement Apply(
        MovementType type,
        AdjustmentReason? reason,
        int onHandDelta,
        int reservedDelta,
        Guid? reservationId,
        Actor actor,
        string correlationId,
        DateTimeOffset now,
        IdempotencyRecord? idempotency)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!TraceText.IsValid(correlationId))
        {
            throw new ArgumentException("A movement needs a valid correlation ID.", nameof(correlationId));
        }

        OnHand += onHandDelta;
        Reserved += reservedDelta;
        if (OnHand < 0 || Reserved < 0 || Reserved > OnHand || OnHand > Quantities.MaxOnHand)
        {
            throw new InvalidOperationException("A stock change broke the inventory invariant.");
        }

        Version++;
        UpdatedAt = now;
        var movement = StockMovement.Record(this, type, reason, onHandDelta, reservedDelta, reservationId, actor, correlationId, now, idempotency);
        _pendingMovements.Add(movement);
        _domainEvents.Add(new StockLevelChanged(this, movement));
        return movement;
    }
}

/// <summary>
/// The domain event of every movement (ADR-028 section 2): the item's identity, the movement and the balance after it.
/// Published as <c>StockLevelChanged</c> v1.
/// </summary>
public sealed record StockLevelChanged(
    Guid InventoryItemId,
    Guid TenantId,
    Guid ProductId,
    Guid VariantId,
    string Sku,
    Guid MovementId,
    MovementType MovementType,
    AdjustmentReason? AdjustmentReason,
    int OnHandDelta,
    int ReservedDelta,
    int OnHand,
    int Reserved,
    long ItemVersion,
    Guid? ReservationId,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    internal StockLevelChanged(InventoryItem item, StockMovement movement)
        : this(
            item.Id,
            item.TenantId,
            item.ProductId,
            item.VariantId,
            item.Sku,
            movement.Id,
            movement.Type,
            movement.Reason,
            movement.OnHandDelta,
            movement.ReservedDelta,
            movement.OnHandAfter,
            movement.ReservedAfter,
            movement.ItemVersion,
            movement.ReservationId,
            movement.OccurredAt)
    {
    }

    public int Available => OnHand - Reserved;
}
