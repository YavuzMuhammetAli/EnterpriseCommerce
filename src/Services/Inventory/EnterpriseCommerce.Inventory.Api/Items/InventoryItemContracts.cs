using System.Text.Json.Serialization;
using EnterpriseCommerce.Inventory.Domain.Items;

namespace EnterpriseCommerce.Inventory.Api.Items;

/// <summary><c>POST /api/v1/inventory/tenants/{tenantId}/items</c> body. The tenant comes from the route only.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateInventoryItemRequest(Guid? ProductId, Guid? VariantId, int? InitialOnHand);

/// <summary><c>POST .../items/{itemId}/adjustments</c> body: a signed change and its reason. Never an absolute balance.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AdjustStockRequest(int? QuantityChange, string? Reason);

/// <summary>An inventory item and its current balance (INVENTORY_API_CONTRACT.md section 3).</summary>
internal sealed record InventoryItemResponse(
    Guid Id,
    Guid TenantId,
    Guid ProductId,
    Guid VariantId,
    string Sku,
    int OnHand,
    int Reserved,
    int Available,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static InventoryItemResponse Of(InventoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new InventoryItemResponse(
            item.Id, item.TenantId, item.ProductId, item.VariantId, item.Sku, item.OnHand, item.Reserved, item.Available, item.Version, item.CreatedAt, item.UpdatedAt);
    }
}

/// <summary>One immutable movement of an item's ledger (INVENTORY_API_CONTRACT.md section 6).</summary>
internal sealed record StockMovementResponse(
    Guid Id,
    Guid InventoryItemId,
    long ItemVersion,
    string Type,
    string? Reason,
    int OnHandDelta,
    int ReservedDelta,
    int OnHandBefore,
    int ReservedBefore,
    int OnHandAfter,
    int ReservedAfter,
    int AvailableAfter,
    Guid? ReservationId,
    string ActorKind,
    string ActorId,
    string CorrelationId,
    DateTimeOffset OccurredAt)
{
    public static StockMovementResponse Of(StockMovement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return new StockMovementResponse(
            movement.Id, movement.InventoryItemId, movement.ItemVersion, movement.Type.ToString(), movement.Reason?.ToString(),
            movement.OnHandDelta, movement.ReservedDelta, movement.OnHandBefore, movement.ReservedBefore, movement.OnHandAfter, movement.ReservedAfter,
            movement.AvailableAfter, movement.ReservationId, movement.ActorKind.ToString(), movement.ActorId, movement.CorrelationId, movement.OccurredAt);
    }
}

/// <summary>An adjustment's answer: its movement and the item's balance now.</summary>
internal sealed record AdjustmentResponse(StockMovementResponse Movement, InventoryItemResponse Item);

/// <summary>One page of a collection (docs/api/README.md, pagination convention).</summary>
internal sealed record PageResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount);
