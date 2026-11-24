using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;

namespace EnterpriseCommerce.Inventory.Domain.Reservations;

/// <summary>The lifecycle of a reservation (ADR-026 section 6). Every state but <see cref="Active"/> is terminal.</summary>
public enum ReservationStatus
{
    Active,
    Committed,
    Released,
    Expired,
}

/// <summary>One requested line: a variant of the tenant and a quantity.</summary>
public sealed record ReservationLineRequest(Guid VariantId, int Quantity);

/// <summary>Why a reservation could not be placed. Nothing changes when one is returned.</summary>
public enum PlacementError
{
    None,

    /// <summary>No line, too many lines, a repeated variant, or a quantity out of range.</summary>
    InvalidLines,

    /// <summary>A line names a variant without an inventory item in the tenant.</summary>
    UnknownVariant,

    /// <summary>At least one line needs more than the available stock.</summary>
    InsufficientStock,
}

/// <summary>The outcome of placing a reservation.</summary>
/// <param name="Reservation">The new reservation, when it was placed.</param>
/// <param name="Error">Why it was not.</param>
/// <param name="ShortVariants">For <see cref="PlacementError.InsufficientStock"/>: every variant that lacks stock.</param>
/// <param name="UnknownVariants">For <see cref="PlacementError.UnknownVariant"/>: every variant without an item.</param>
public sealed record PlacementResult(Reservation? Reservation, PlacementError Error, IReadOnlyList<Guid> ShortVariants, IReadOnlyList<Guid> UnknownVariants)
{
    internal static PlacementResult Refused(PlacementError error, IReadOnlyList<Guid>? shortVariants = null, IReadOnlyList<Guid>? unknownVariants = null) =>
        new(null, error, shortVariants ?? [], unknownVariants ?? []);
}

/// <summary>What a commit, release or expiry request did.</summary>
public enum TransitionOutcome
{
    /// <summary>The reservation moved to the requested state.</summary>
    Changed,

    /// <summary>The reservation was already in the requested state (an idempotent repetition), or already freed.</summary>
    Unchanged,

    /// <summary>The reservation was active but overdue: it expired now, in this request (lazy expiry, ADR-026 section 7).</summary>
    ExpiredNow,

    /// <summary>The reservation is in a terminal state that forbids the request: nothing changed.</summary>
    Conflict,
}

/// <summary>The outcome of a transition request and the reservation's state after it.</summary>
public readonly record struct TransitionResult(TransitionOutcome Outcome, ReservationStatus Status);

/// <summary>One reserved line: an inventory item of the reservation's tenant and its quantity.</summary>
public sealed class ReservationLine
{
    private ReservationLine()
    {
    }

    public Guid ReservationId { get; private set; }

    public Guid InventoryItemId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantId { get; private set; }

    public int Quantity { get; private set; }

    internal static ReservationLine For(Guid reservationId, InventoryItem item, int quantity) => new()
    {
        ReservationId = reservationId,
        InventoryItemId = item.Id,
        ProductId = item.ProductId,
        VariantId = item.VariantId,
        Quantity = quantity,
    };
}

/// <summary>
/// A temporary commitment of stock to a service client, for future checkout orchestration (ADR-026 sections 6 to 8).
/// It belongs to one tenant and one owner client, holds 1 to 50 lines of distinct variants, and ends exactly once:
/// committed, released or expired. Terminal states are never reopened.
/// </summary>
/// <remarks>
/// Every transition changes the lines' items in the same call, so the caller must hold the locks of the reservation and
/// of every line's item (ADR-026 section 9). The items are passed in by ID; a missing item is a broken invariant.
/// </remarks>
public sealed class Reservation : IHasDomainEvents
{
    /// <summary>The most lines of one reservation (ADR-026 section 6; it also bounds the event size, ADR-028 section 2).</summary>
    public const int MaxLines = 50;

    private readonly List<IDomainEvent> _domainEvents = [];
    private readonly List<ReservationLine> _lines = [];

    private Reservation()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The service client that created the reservation: the only one that may read or change it.</summary>
    public string OwnerClientId { get; private set; } = string.Empty;

    /// <summary>With the owner and the tenant, the idempotency scope of the creation (ADR-026 section 8).</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    public byte[] RequestHash { get; private set; } = [];

    /// <summary>The caller's opaque reference, for example an order ID; never personal data.</summary>
    public string? Reference { get; private set; }

    public ReservationStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the reservation reached its terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>1 at creation, incremented by the terminal transition; published (ADR-028 section 3).</summary>
    public long Version { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public IReadOnlyList<ReservationLine> Lines => _lines;

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    public bool IsTerminal => Status != ReservationStatus.Active;

    /// <summary>Whether an active reservation's expiry has passed at <paramref name="now"/>, the database time.</summary>
    public bool IsOverdue(DateTimeOffset now) => Status == ReservationStatus.Active && now >= ExpiresAt;

    /// <summary>Whether the requested lines are well formed: 1 to 50 lines, distinct variants, quantities in range.</summary>
    public static bool AreValidLines(IReadOnlyList<ReservationLineRequest>? lines) =>
        lines is { Count: >= 1 and <= MaxLines }
        && lines.All(line => line.VariantId != Guid.Empty && Quantities.IsValidLineQuantity(line.Quantity))
        && lines.Select(line => line.VariantId).Distinct().Count() == lines.Count;

    /// <summary>
    /// Places a reservation atomically: every line is checked against the locked items first, and only if all fit are
    /// the items changed (ADR-026 section 6). A refused placement changes nothing.
    /// </summary>
    /// <param name="tenantId">The tenant of the request; every item must belong to it.</param>
    /// <param name="ownerClientId">The creating service client.</param>
    /// <param name="idempotency">The key and request hash.</param>
    /// <param name="lines">The requested lines.</param>
    /// <param name="itemsByVariant">The tenant's locked items of the requested variants. A variant without an item fails the request.</param>
    /// <param name="reference">The caller's reference, or <c>null</c>.</param>
    /// <param name="lifetime">How long the reservation holds the stock.</param>
    /// <param name="correlationId">The request's correlation ID.</param>
    /// <param name="now">The database time.</param>
    public static PlacementResult Place(
        Guid tenantId,
        string ownerClientId,
        IdempotencyRecord idempotency,
        IReadOnlyList<ReservationLineRequest> lines,
        IReadOnlyDictionary<Guid, InventoryItem> itemsByVariant,
        string? reference,
        TimeSpan lifetime,
        string correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(idempotency);
        ArgumentNullException.ThrowIfNull(itemsByVariant);
        if (tenantId == Guid.Empty || lifetime <= TimeSpan.Zero || (reference is not null && !TraceText.IsValid(reference)))
        {
            throw new ArgumentException("A reservation needs a tenant, a positive lifetime and a well-formed reference.");
        }

        var owner = Actor.Service(ownerClientId);
        if (!AreValidLines(lines))
        {
            return PlacementResult.Refused(PlacementError.InvalidLines);
        }

        var unknown = lines
            .Where(line => !itemsByVariant.TryGetValue(line.VariantId, out var item) || item.TenantId != tenantId || item.VariantId != line.VariantId)
            .Select(line => line.VariantId)
            .ToList();
        if (unknown.Count > 0)
        {
            return PlacementResult.Refused(PlacementError.UnknownVariant, unknownVariants: unknown);
        }

        // Check every line before changing any: a multi-line reservation is all or nothing.
        var shortVariants = lines.Where(line => !itemsByVariant[line.VariantId].CanReserve(line.Quantity)).Select(line => line.VariantId).ToList();
        if (shortVariants.Count > 0)
        {
            return PlacementResult.Refused(PlacementError.InsufficientStock, shortVariants: shortVariants);
        }

        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            OwnerClientId = owner.Id,
            IdempotencyKey = idempotency.Key,
            RequestHash = idempotency.RequestHash,
            Reference = reference,
            Status = ReservationStatus.Active,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            Version = 1,
            CorrelationId = correlationId,
        };

        // Lines in ascending item order: the lock order of ADR-026 section 9, and a stable line order.
        foreach (var line in lines.OrderBy(line => itemsByVariant[line.VariantId].Id))
        {
            var item = itemsByVariant[line.VariantId];
            var change = item.Reserve(line.Quantity, reservation.Id, owner, correlationId, now);
            if (!change.Succeeded)
            {
                throw new InvalidOperationException("A checked reservation line could not be reserved.");
            }

            reservation._lines.Add(ReservationLine.For(reservation.Id, item, line.Quantity));
        }

        reservation._domainEvents.Add(new ReservationCreated(reservation));
        return new PlacementResult(reservation, PlacementError.None, [], []);
    }

    /// <summary>
    /// Commits an active reservation: each line's units leave stock (ADR-026 section 6). Idempotent on
    /// <see cref="ReservationStatus.Committed"/>; a conflict on <see cref="ReservationStatus.Released"/> and
    /// <see cref="ReservationStatus.Expired"/>. An overdue active reservation expires instead, and is not committed.
    /// </summary>
    public TransitionResult Commit(IReadOnlyDictionary<Guid, InventoryItem> items, Actor actor, string correlationId, DateTimeOffset now)
    {
        switch (Status)
        {
            case ReservationStatus.Committed:
                return new TransitionResult(TransitionOutcome.Unchanged, Status);
            case ReservationStatus.Released or ReservationStatus.Expired:
                return new TransitionResult(TransitionOutcome.Conflict, Status);
        }

        EnsureLinesLoaded(items);
        if (IsOverdue(now))
        {
            ExpireLines(items, correlationId, now);
            return new TransitionResult(TransitionOutcome.ExpiredNow, Status);
        }

        foreach (var line in _lines)
        {
            ItemOf(items, line).CommitReserved(line.Quantity, Id, actor, correlationId, now);
        }

        Complete(ReservationStatus.Committed, now);
        _domainEvents.Add(new ReservationCommitted(this));
        return new TransitionResult(TransitionOutcome.Changed, Status);
    }

    /// <summary>
    /// Releases an active reservation: each line's units become available again, on-hand stock is unchanged. Idempotent
    /// on <see cref="ReservationStatus.Released"/>, and answers <see cref="TransitionOutcome.Unchanged"/> on
    /// <see cref="ReservationStatus.Expired"/>, because the stock is already free; a conflict on
    /// <see cref="ReservationStatus.Committed"/>. An overdue active reservation expires instead.
    /// </summary>
    public TransitionResult Release(IReadOnlyDictionary<Guid, InventoryItem> items, Actor actor, string correlationId, DateTimeOffset now)
    {
        switch (Status)
        {
            case ReservationStatus.Released or ReservationStatus.Expired:
                return new TransitionResult(TransitionOutcome.Unchanged, Status);
            case ReservationStatus.Committed:
                return new TransitionResult(TransitionOutcome.Conflict, Status);
        }

        EnsureLinesLoaded(items);
        if (IsOverdue(now))
        {
            ExpireLines(items, correlationId, now);
            return new TransitionResult(TransitionOutcome.ExpiredNow, Status);
        }

        foreach (var line in _lines)
        {
            ItemOf(items, line).ReturnReserved(line.Quantity, MovementType.ReservationRelease, Id, actor, correlationId, now);
        }

        Complete(ReservationStatus.Released, now);
        _domainEvents.Add(new ReservationReleased(this));
        return new TransitionResult(TransitionOutcome.Changed, Status);
    }

    /// <summary>
    /// Expires an overdue active reservation: the expiry worker's transition. Anything else is left unchanged, so a
    /// reservation committed, released or expired meanwhile is never transitioned twice.
    /// </summary>
    public TransitionResult Expire(IReadOnlyDictionary<Guid, InventoryItem> items, string correlationId, DateTimeOffset now)
    {
        if (!IsOverdue(now))
        {
            return new TransitionResult(TransitionOutcome.Unchanged, Status);
        }

        EnsureLinesLoaded(items);
        ExpireLines(items, correlationId, now);
        return new TransitionResult(TransitionOutcome.Changed, Status);
    }

    public void ClearDomainEvents() => _domainEvents.Clear();

    private void ExpireLines(IReadOnlyDictionary<Guid, InventoryItem> items, string correlationId, DateTimeOffset now)
    {
        foreach (var line in _lines)
        {
            ItemOf(items, line).ReturnReserved(line.Quantity, MovementType.ReservationExpiry, Id, Actor.ReservationExpiry, correlationId, now);
        }

        Complete(ReservationStatus.Expired, now);
        _domainEvents.Add(new ReservationExpired(this));
    }

    private void Complete(ReservationStatus status, DateTimeOffset now)
    {
        Status = status;
        CompletedAt = now;
        Version++;
    }

    /// <summary>Every line's item must be present before any changes, so a missing one changes nothing.</summary>
    private void EnsureLinesLoaded(IReadOnlyDictionary<Guid, InventoryItem> items)
    {
        foreach (var line in _lines)
        {
            ItemOf(items, line);
        }
    }

    private InventoryItem ItemOf(IReadOnlyDictionary<Guid, InventoryItem> items, ReservationLine line) =>
        items.TryGetValue(line.InventoryItemId, out var item) && item.TenantId == TenantId
            ? item
            : throw new InvalidOperationException("A reservation line's item was not loaded and locked.");
}
