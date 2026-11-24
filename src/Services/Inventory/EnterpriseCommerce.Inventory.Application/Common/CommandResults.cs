namespace EnterpriseCommerce.Inventory.Application.Common;

/// <summary>The outcome of an Inventory use case; the API maps each to its status and problem (INVENTORY_API_CONTRACT.md section 9).</summary>
public enum CommandStatus
{
    /// <summary>Done: a read, or a state transition that changed something or was already in effect.</summary>
    Ok,

    /// <summary>A new resource was created.</summary>
    Created,

    /// <summary>The same idempotency key and request were seen before: the original resource, in its current state.</summary>
    Replayed,

    /// <summary>The request is malformed; see the errors.</summary>
    Invalid,

    /// <summary>No active membership of the caller in the tenant, or no such tenant (<c>404 tenant-not-found</c>).</summary>
    TenantNotVisible,

    /// <summary>An active member whose role lacks the permission (<c>403</c>).</summary>
    PermissionDenied,

    /// <summary>The addressed resource does not exist in the tenant (or, for a reservation, for the caller).</summary>
    NotFound,

    /// <summary>The idempotency key was used for a different request (<c>422</c>).</summary>
    IdempotencyKeyReused,

    /// <summary>The tenant already tracks the variant (<c>409 inventory-item-exists</c>).</summary>
    ItemExists,

    /// <summary>Catalog does not know the variant in the tenant's product (<c>422 variant-not-found</c>).</summary>
    VariantNotFound,

    /// <summary>The product is archived, or its status is unknown (<c>409 variant-not-eligible</c>, ADR-027 section 2).</summary>
    VariantNotEligible,

    /// <summary>The adjustment would leave on-hand stock below the reserved quantity (<c>409</c>).</summary>
    InsufficientUnreservedStock,

    /// <summary>The adjustment would exceed the on-hand maximum (<c>409 stock-limit-exceeded</c>).</summary>
    StockLimitExceeded,

    /// <summary>A reservation line names a variant without an inventory item in the tenant (<c>422</c>).</summary>
    InventoryItemNotFound,

    /// <summary>At least one reservation line lacks available stock (<c>409 insufficient-stock</c>).</summary>
    InsufficientStock,

    /// <summary>The reservation is committed: it cannot be released (<c>409</c>).</summary>
    ReservationCommitted,

    /// <summary>The reservation was released: it cannot be committed (<c>409</c>).</summary>
    ReservationReleased,

    /// <summary>The reservation expired, possibly during this request: it cannot be committed (<c>409</c>).</summary>
    ReservationExpired,

    /// <summary>A concurrent change won; a backstop the row locks make unexpected (<c>409</c>).</summary>
    ConcurrencyConflict,
}

/// <summary>A use case's outcome, its value when it has one, and its validation errors or affected variants.</summary>
public sealed record CommandResult<T>(
    CommandStatus Status,
    T? Value = default,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    IReadOnlyList<Guid>? Variants = null)
{
    public static CommandResult<T> Of(CommandStatus status) => new(status);

    public static CommandResult<T> Invalid(IReadOnlyDictionary<string, string[]> errors) => new(CommandStatus.Invalid, Errors: errors);
}
