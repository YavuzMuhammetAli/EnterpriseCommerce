using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;

namespace EnterpriseCommerce.Inventory.Application.Common;

/// <summary>Whether a write's staged changes are committed, and what the operation returns.</summary>
public readonly record struct WriteDecision<TResult>(bool ShouldCommit, TResult Result);

public static class WriteDecision
{
    public static WriteDecision<TResult> Commit<TResult>(TResult result) => new(true, result);

    public static WriteDecision<TResult> Rollback<TResult>(TResult result) => new(false, result);
}

/// <summary>Why a write was refused by the database's own rules rather than by the domain.</summary>
public enum InventoryWriteConflict
{
    /// <summary>A unique index refused the change, for example a second item for one variant, or a reused key.</summary>
    UniqueConstraint,

    /// <summary>A <c>rowversion</c> check failed: a concurrent change won. A backstop; the row locks prevent it.</summary>
    Concurrency,
}

/// <summary>A write was refused by a database constraint; nothing was committed.</summary>
public sealed class InventoryWriteConflictException(InventoryWriteConflict conflict, string message, Exception innerException)
    : Exception(message, innerException)
{
    public InventoryWriteConflict Conflict { get; } = conflict;
}

/// <summary>
/// Runs one Inventory write as one short transaction (ADR-026 section 9): <c>READ COMMITTED</c>, bounded lock waits, and
/// a whole-transaction retry when SQL Server picks it as a deadlock victim. The work locks the rows it decides on
/// through the session, in the fixed order of ADR-026 section 9, and returns whether to commit.
/// </summary>
/// <remarks>
/// The work may run more than once (a deadlock retry), so it must not have effects outside the session. A lock wait
/// beyond the timeout, an unreachable database or exhausted retries throw <see cref="DataStoreUnavailableException"/>;
/// a refused constraint throws <see cref="InventoryWriteConflictException"/>.
/// </remarks>
public interface IInventoryWriteGate
{
    /// <param name="operation">A stable operation name, for logs.</param>
    /// <param name="work">The decision, run inside the transaction.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task<TResult> ExecuteAsync<TResult>(
        string operation,
        Func<IInventoryWriteSession, CancellationToken, Task<WriteDecision<TResult>>> work,
        CancellationToken cancellationToken);
}

/// <summary>
/// The reads and staged changes of one write transaction. Every read is scoped by the tenant it is given; the locking
/// reads hold an update lock until the transaction ends and see the latest committed row, also under
/// <c>READ_COMMITTED_SNAPSHOT</c>.
/// </summary>
public interface IInventoryWriteSession
{
    /// <summary>The database time (<c>SYSUTCDATETIME()</c>) read when the transaction began: the clock of every decision.</summary>
    DateTimeOffset Now { get; }

    /// <summary>The correlation and causation IDs of the integration events this transaction writes. Required before a save with events.</summary>
    void UseMessageMetadata(MessageMetadata metadata);

    /// <summary>Locks and loads one item of the tenant, or returns <c>null</c>.</summary>
    Task<InventoryItem?> LockItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks and loads the tenant's items of the given variants, one at a time in ascending item ID order (the lock
    /// order). Variants without an item are absent from the result, keyed by variant ID.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, InventoryItem>> LockItemsOfVariantsAsync(Guid tenantId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken);

    /// <summary>Locks and loads the tenant's items by ID, in ascending ID order, keyed by item ID.</summary>
    Task<IReadOnlyDictionary<Guid, InventoryItem>> LockItemsAsync(Guid tenantId, IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    /// <summary>Locks and loads a reservation of the tenant with its lines, or returns <c>null</c>.</summary>
    Task<Reservation?> LockReservationAsync(Guid tenantId, Guid reservationId, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the active reservation of any tenant whose expiry has passed longest ago, skipping rows other transactions
    /// hold (<c>READPAST</c>), locked with its lines; <c>null</c> when none is due (ADR-026 section 7).
    /// </summary>
    Task<Reservation?> ClaimOverdueReservationAsync(CancellationToken cancellationToken);

    /// <summary>Serializes the requests of one reservation idempotency scope (client, tenant, key) until the transaction ends.</summary>
    Task LockReservationKeyAsync(string clientId, Guid tenantId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The reservation created in the scope (client, tenant, key), with its lines.</summary>
    Task<Reservation?> FindReservationByKeyAsync(string clientId, Guid tenantId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The adjustment movement created in the scope (tenant, subject, key).</summary>
    Task<StockMovement?> FindAdjustmentByKeyAsync(Guid tenantId, string subjectId, string idempotencyKey, CancellationToken cancellationToken);

    void Add(InventoryItem item);

    void Add(Reservation reservation);
}
