using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;

namespace EnterpriseCommerce.Inventory.Application.Items;

/// <summary>A merchant listing filter. Every query is scoped to one tenant (ADR-014); <c>Sku</c> is an exact match.</summary>
public sealed record InventoryItemFilter(Guid? ProductId, Guid? VariantId, string? Sku);

/// <summary>
/// The read side (selective CQRS, ADR-026 section 12): untracked reads of committed state, never a lock. Every read
/// names the tenant, so another tenant's item or reservation is never found, whatever its ID. Nothing here authorizes.
/// </summary>
public interface IInventoryQueries
{
    Task<InventoryItem?> GetItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken);

    /// <summary>The tenant's items in creation order (then by ID).</summary>
    Task<Page<InventoryItem>> ListItemsAsync(Guid tenantId, InventoryItemFilter filter, PageRequest page, CancellationToken cancellationToken);

    /// <summary>An item's movements, newest first (descending item version).</summary>
    Task<Page<StockMovement>> ListMovementsAsync(Guid tenantId, Guid itemId, PageRequest page, CancellationToken cancellationToken);

    /// <summary>The item created by this subject in this tenant with this <c>Idempotency-Key</c>, if any.</summary>
    Task<InventoryItem?> FindItemByCreationKeyAsync(Guid tenantId, string subjectId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>The adjustment created by this subject in this tenant with this <c>Idempotency-Key</c>, if any.</summary>
    Task<StockMovement?> FindAdjustmentByKeyAsync(Guid tenantId, string subjectId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>A reservation of the tenant owned by the client, with its lines; another client's is never found.</summary>
    Task<Reservation?> GetReservationAsync(Guid tenantId, string ownerClientId, Guid reservationId, CancellationToken cancellationToken);

    /// <summary>The reservation created by the client in the tenant with this <c>Idempotency-Key</c>, with its lines.</summary>
    Task<Reservation?> FindReservationByKeyAsync(Guid tenantId, string ownerClientId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Active reservations whose expiry passed before <paramref name="before"/>, by database time (for health).</summary>
    Task<int> CountOverdueReservationsAsync(TimeSpan before, CancellationToken cancellationToken);
}
