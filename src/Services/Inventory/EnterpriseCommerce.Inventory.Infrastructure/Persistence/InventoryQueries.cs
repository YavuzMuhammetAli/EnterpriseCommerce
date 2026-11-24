using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Application.Items;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.Inventory.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IInventoryQueries"/>: untracked reads of committed state, each scoped by its
/// tenant, with SQL Server failures answered as <see cref="DataStoreUnavailableException"/> (<c>503</c>).
/// </summary>
internal sealed class InventoryQueries(InventoryDbContext context) : IInventoryQueries
{
    public Task<InventoryItem?> GetItemAsync(Guid tenantId, Guid itemId, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("read inventory item", () =>
            context.InventoryItems.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Id == itemId, cancellationToken));

    public Task<Page<InventoryItem>> ListItemsAsync(Guid tenantId, InventoryItemFilter filter, PageRequest page, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("list inventory items", async () =>
        {
            ArgumentNullException.ThrowIfNull(filter);
            ArgumentNullException.ThrowIfNull(page);

            var query = context.InventoryItems.AsNoTracking().Where(item => item.TenantId == tenantId);
            if (filter.ProductId is { } productId)
            {
                query = query.Where(item => item.ProductId == productId);
            }

            if (filter.VariantId is { } variantId)
            {
                query = query.Where(item => item.VariantId == variantId);
            }

            if (filter.Sku is { Length: > 0 } sku)
            {
                query = query.Where(item => item.Sku == sku);
            }

            var total = await query.LongCountAsync(cancellationToken);
            var items = await query.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
            return new Page<InventoryItem>(items, page.Page, page.PageSize, total);
        });

    public Task<Page<StockMovement>> ListMovementsAsync(Guid tenantId, Guid itemId, PageRequest page, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("list stock movements", async () =>
        {
            ArgumentNullException.ThrowIfNull(page);

            var query = context.StockMovements.AsNoTracking().Where(movement => movement.TenantId == tenantId && movement.InventoryItemId == itemId);
            var total = await query.LongCountAsync(cancellationToken);
            var movements = await query.OrderByDescending(movement => movement.ItemVersion).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
            return new Page<StockMovement>(movements, page.Page, page.PageSize, total);
        });

    public Task<InventoryItem?> FindItemByCreationKeyAsync(Guid tenantId, string subjectId, string idempotencyKey, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("find inventory item by key", () =>
            context.InventoryItems.AsNoTracking().SingleOrDefaultAsync(
                item => item.TenantId == tenantId && item.CreatedBySubject == subjectId && item.CreationKey == idempotencyKey,
                cancellationToken));

    public Task<StockMovement?> FindAdjustmentByKeyAsync(Guid tenantId, string subjectId, string idempotencyKey, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("find adjustment by key", () =>
            context.StockMovements.AsNoTracking().SingleOrDefaultAsync(
                movement => movement.TenantId == tenantId && movement.ActorId == subjectId && movement.IdempotencyKey == idempotencyKey,
                cancellationToken));

    public Task<Reservation?> GetReservationAsync(Guid tenantId, string ownerClientId, Guid reservationId, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("read reservation", () =>
            context.Reservations.AsNoTracking().Include(reservation => reservation.Lines).SingleOrDefaultAsync(
                reservation => reservation.TenantId == tenantId && reservation.OwnerClientId == ownerClientId && reservation.Id == reservationId,
                cancellationToken));

    public Task<Reservation?> FindReservationByKeyAsync(Guid tenantId, string ownerClientId, string idempotencyKey, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("find reservation by key", () =>
            context.Reservations.AsNoTracking().Include(reservation => reservation.Lines).SingleOrDefaultAsync(
                reservation => reservation.TenantId == tenantId && reservation.OwnerClientId == ownerClientId && reservation.IdempotencyKey == idempotencyKey,
                cancellationToken));

    public Task<int> CountOverdueReservationsAsync(TimeSpan before, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("count overdue reservations", async () =>
        {
            var seconds = (int)Math.Clamp(before.TotalSeconds, 0, int.MaxValue);

            // Database time, and the literal of the filtered index of active reservations.
            return (await context.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [Reservations] WHERE [Status] = 'Active' AND [ExpiresAt] <= DATEADD(SECOND, -{seconds}, TODATETIMEOFFSET(SYSUTCDATETIME(), 0))")
                .ToListAsync(cancellationToken)).Single();
        });
}
