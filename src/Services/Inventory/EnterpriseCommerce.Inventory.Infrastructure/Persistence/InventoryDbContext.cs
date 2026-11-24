using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;
using EnterpriseCommerce.Inventory.Infrastructure.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.Infrastructure.Persistence;

/// <summary>
/// The Inventory database (database per service, ADR-003 and ADR-011). It holds no foreign key or reference to any other
/// service's database: Catalog's product and variant IDs and Identity subjects are opaque external identifiers.
/// </summary>
/// <remarks>
/// <para>
/// The invariants of ADR-026 are database constraints as well as domain rules: <c>0 ≤ Reserved ≤ OnHand ≤ max</c>, one
/// item per (tenant, variant), linear movements (<c>after = before + delta</c>, one per item version), the reservation
/// status rules, and the idempotency scopes as unique indexes. Mutable tables have a <c>rowversion</c>. Triggers make
/// movements append-only and reservation lines immutable.
/// </para>
/// <para>
/// Saving adds, before the base save, every tracked item's pending movements and the outbox rows of every tracked
/// aggregate's domain events, so a balance change, its ledger entry and its integration events commit together or not
/// at all (ADR-026 section 5, ADR-023 section 5). A changed item without a movement fails the save.
/// </para>
/// </remarks>
internal sealed class InventoryDbContext(
    DbContextOptions<InventoryDbContext> options,
    IOptions<OutboxOptions>? outboxOptions = null) : DbContext(options)
{
    public const string RowVersion = "RowVersion";
    public const string ItemVariantIndex = "UX_InventoryItems_TenantId_VariantId";
    public const string ItemCreationKeyIndex = "UX_InventoryItems_TenantId_CreatedBySubject_CreationKey";
    public const string MovementVersionIndex = "UX_StockMovements_InventoryItemId_ItemVersion";
    public const string AdjustmentKeyIndex = "UX_StockMovements_TenantId_ActorId_IdempotencyKey";
    public const string ReservationKeyIndex = "UX_Reservations_OwnerClientId_TenantId_IdempotencyKey";
    public const string ActiveReservationExpiryIndex = "IX_Reservations_Active_ExpiresAt";

    /// <summary>Refuses updates and deletes of movements (error 50101).</summary>
    public const string MovementAppendOnlyTrigger = "TR_StockMovements_AppendOnly";

    /// <summary>Refuses updates and deletes of reservation lines (error 50102).</summary>
    public const string ReservationLineImmutableTrigger = "TR_ReservationLines_Immutable";

    private readonly string _outboxTopic = outboxOptions?.Value.Topic is { Length: > 0 } topic ? topic : InventoryOutbox.DefaultTopic;
    private MessageMetadata? _messageMetadata;

    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();

    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    public DbSet<ReservationLine> ReservationLines => Set<ReservationLine>();

    /// <summary>The transactional outbox (ADR-023 section 5). Written only by the save, read only by the relay.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// The correlation and causation IDs of the integration events this context's next saves write. A save with domain
    /// events and no metadata fails closed.
    /// </summary>
    public void UseMessageMetadata(MessageMetadata? metadata) => _messageMetadata = metadata;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StageMovementsAndOutbox();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StageMovementsAndOutbox();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Adds the pending movements and the outbox rows of the tracked aggregates, then forgets them. If the save then
    /// fails, nothing was written; a retry in the same context writes them once, because they are already staged.
    /// </summary>
    private void StageMovementsAndOutbox()
    {
        foreach (var entry in ChangeTracker.Entries<InventoryItem>().ToList())
        {
            var item = entry.Entity;
            if (entry.State == EntityState.Modified && entry.Property(i => i.Version).IsModified && item.PendingMovements.Count == 0
                && !StockMovements.Local.Any(movement => movement.InventoryItemId == item.Id && movement.ItemVersion == item.Version))
            {
                throw new InvalidOperationException("An inventory item changed without a movement; nothing was saved.");
            }

            StockMovements.AddRange(item.PendingMovements);
            item.ClearPendingMovements();
        }

        var aggregates = ChangeTracker.Entries<IHasDomainEvents>()
            .Select(entry => entry.Entity)
            .Where(aggregate => aggregate.DomainEvents.Count > 0)
            .ToList();
        if (aggregates.Count == 0)
        {
            return;
        }

        var metadata = _messageMetadata
            ?? throw new InvalidOperationException("Integration events need message metadata (correlation and causation IDs); nothing was saved.");
        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                OutboxMessages.AddRange(InventoryIntegrationEvents.ToOutboxMessages(domainEvent, metadata, _outboxTopic));
            }

            aggregate.ClearDomainEvents();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        MapItems(modelBuilder);
        MapMovements(modelBuilder);
        MapReservations(modelBuilder);
        modelBuilder.MapOutboxMessages();
    }

    private static void MapItems(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<InventoryItem>(item =>
        {
            item.ToTable("InventoryItems", table =>
            {
                table.HasCheckConstraint("CK_InventoryItems_OnHand", $"[OnHand] >= 0 AND [OnHand] <= {Quantities.MaxOnHand}");
                table.HasCheckConstraint("CK_InventoryItems_Reserved", "[Reserved] >= 0 AND [Reserved] <= [OnHand]");
                table.HasCheckConstraint("CK_InventoryItems_Version", "[Version] >= 1");
            });
            item.HasKey(i => i.Id);
            item.Property(i => i.Id).ValueGeneratedNever();

            // Derived or transient: never stored.
            item.Ignore(i => i.Available);
            item.Ignore(i => i.DomainEvents);
            item.Ignore(i => i.PendingMovements);

            item.Property(i => i.Sku).HasMaxLength(SkuSnapshot.MaxLength).IsUnicode(false);
            item.Property(i => i.CreatedBySubject).HasMaxLength(Actor.IdMaxLength);
            item.Property(i => i.CreationKey).HasMaxLength(IdempotencyRecord.KeyMaxLength);
            item.Property(i => i.CreationRequestHash).HasMaxLength(IdempotencyRecord.HashLength).IsFixedLength();
            item.Property<byte[]>(RowVersion).IsRowVersion();

            // One balance per variant and tenant (ADR-026 section 2), and the creation's idempotency scope (section 8).
            item.HasIndex(i => new { i.TenantId, i.VariantId }).IsUnique().HasDatabaseName(ItemVariantIndex);
            item.HasIndex(i => new { i.TenantId, i.CreatedBySubject, i.CreationKey }).IsUnique().HasDatabaseName(ItemCreationKeyIndex);

            // The tenant's listing in creation order, and its product and SKU filters. Creation time, not the version 7 ID:
            // SQL Server orders uniqueidentifier values by their last bytes first, which is not the time order.
            item.HasIndex(i => new { i.TenantId, i.CreatedAt, i.Id }).HasDatabaseName("IX_InventoryItems_TenantId_CreatedAt_Id");
            item.HasIndex(i => new { i.TenantId, i.ProductId }).HasDatabaseName("IX_InventoryItems_TenantId_ProductId");
            item.HasIndex(i => new { i.TenantId, i.Sku }).HasDatabaseName("IX_InventoryItems_TenantId_Sku");
        });

    private static void MapMovements(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<StockMovement>(movement =>
        {
            movement.ToTable("StockMovements", table =>
            {
                table.HasCheckConstraint("CK_StockMovements_Type", AllowedValues<MovementType>("Type"));
                table.HasCheckConstraint("CK_StockMovements_Reason", $"[Reason] IS NULL OR {AllowedValues<AdjustmentReason>("Reason")}");
                table.HasCheckConstraint("CK_StockMovements_ActorKind", AllowedValues<ActorKind>("ActorKind"));

                // Linear: after = before + delta, and the after values keep the item invariant (ADR-026 section 5).
                table.HasCheckConstraint(
                    "CK_StockMovements_Balance",
                    $"[OnHandAfter] = [OnHandBefore] + [OnHandDelta] AND [ReservedAfter] = [ReservedBefore] + [ReservedDelta]"
                    + $" AND [OnHandAfter] >= 0 AND [OnHandAfter] <= {Quantities.MaxOnHand} AND [ReservedAfter] >= 0 AND [ReservedAfter] <= [OnHandAfter]");
                table.HasCheckConstraint("CK_StockMovements_ItemVersion", "[ItemVersion] >= 1");
                table.HasTrigger(MovementAppendOnlyTrigger);

                // An adjustment has a reason and a key; a reservation movement has its reservation; nothing else has either.
                table.HasCheckConstraint(
                    "CK_StockMovements_Shape",
                    $"([Type] = N'{nameof(MovementType.Adjustment)}' AND [Reason] IS NOT NULL AND [IdempotencyKey] IS NOT NULL AND [RequestHash] IS NOT NULL AND [ReservationId] IS NULL)"
                    + $" OR ([Type] = N'{nameof(MovementType.InitialStock)}' AND [Reason] IS NULL AND [IdempotencyKey] IS NULL AND [ReservationId] IS NULL)"
                    + $" OR ([Type] IN (N'{nameof(MovementType.Reservation)}', N'{nameof(MovementType.ReservationRelease)}', N'{nameof(MovementType.ReservationExpiry)}', N'{nameof(MovementType.ReservationCommit)}')"
                    + " AND [Reason] IS NULL AND [IdempotencyKey] IS NULL AND [ReservationId] IS NOT NULL)");
            });
            movement.HasKey(m => m.Id);
            movement.Property(m => m.Id).ValueGeneratedNever();
            movement.Ignore(m => m.AvailableAfter);
            movement.Property(m => m.Type).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
            movement.Property(m => m.Reason).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
            movement.Property(m => m.ActorKind).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            movement.Property(m => m.ActorId).HasMaxLength(Actor.IdMaxLength);
            movement.Property(m => m.CorrelationId).HasMaxLength(TraceText.MaxLength).IsUnicode(false);
            movement.Property(m => m.IdempotencyKey).HasMaxLength(IdempotencyRecord.KeyMaxLength);
            movement.Property(m => m.RequestHash).HasMaxLength(IdempotencyRecord.HashLength).IsFixedLength();

            // Append-only: no rowversion, because a movement is never updated (the trigger refuses it).
            movement.HasOne<InventoryItem>().WithMany().HasForeignKey(m => m.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
            movement.HasOne<Reservation>().WithMany().HasForeignKey(m => m.ReservationId).OnDelete(DeleteBehavior.Restrict);

            // One movement per item version (the ledger is linear); the history query reads it newest first.
            movement.HasIndex(m => new { m.InventoryItemId, m.ItemVersion }).IsUnique().HasDatabaseName(MovementVersionIndex)
                .IncludeProperties(m => m.TenantId);
            movement.HasIndex(m => new { m.TenantId, m.ActorId, m.IdempotencyKey }).IsUnique()
                .HasFilter("[IdempotencyKey] IS NOT NULL").HasDatabaseName(AdjustmentKeyIndex);
            movement.HasIndex(m => m.ReservationId).HasFilter("[ReservationId] IS NOT NULL").HasDatabaseName("IX_StockMovements_ReservationId");
        });

    private static void MapReservations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Reservation>(reservation =>
        {
            reservation.ToTable("Reservations", table =>
            {
                table.HasCheckConstraint("CK_Reservations_Status", AllowedValues<ReservationStatus>("Status"));
                table.HasCheckConstraint(
                    "CK_Reservations_Completion",
                    $"([Status] = N'{nameof(ReservationStatus.Active)}' AND [CompletedAt] IS NULL) OR ([Status] <> N'{nameof(ReservationStatus.Active)}' AND [CompletedAt] IS NOT NULL)");
                table.HasCheckConstraint("CK_Reservations_Expiry", "[ExpiresAt] > [CreatedAt]");
                table.HasCheckConstraint("CK_Reservations_Version", "[Version] >= 1");
            });
            reservation.HasKey(r => r.Id);
            reservation.Property(r => r.Id).ValueGeneratedNever();
            reservation.Ignore(r => r.DomainEvents);
            reservation.Ignore(r => r.IsTerminal);
            reservation.Property(r => r.OwnerClientId).HasMaxLength(Actor.IdMaxLength).IsUnicode(false);
            reservation.Property(r => r.IdempotencyKey).HasMaxLength(IdempotencyRecord.KeyMaxLength);
            reservation.Property(r => r.RequestHash).HasMaxLength(IdempotencyRecord.HashLength).IsFixedLength();
            reservation.Property(r => r.Reference).HasMaxLength(TraceText.MaxLength).IsUnicode(false);
            reservation.Property(r => r.Status).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            reservation.Property(r => r.CorrelationId).HasMaxLength(TraceText.MaxLength).IsUnicode(false);
            reservation.Property<byte[]>(RowVersion).IsRowVersion();

            reservation.HasMany(r => r.Lines).WithOne().HasForeignKey(line => line.ReservationId).OnDelete(DeleteBehavior.Restrict);
            reservation.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).HasField("_lines");

            // The idempotency scope (ADR-026 section 8), and the expiry worker's claim (section 7).
            reservation.HasIndex(r => new { r.OwnerClientId, r.TenantId, r.IdempotencyKey }).IsUnique().HasDatabaseName(ReservationKeyIndex);
            reservation.HasIndex(r => r.ExpiresAt).HasFilter($"[Status] = '{nameof(ReservationStatus.Active)}'").HasDatabaseName(ActiveReservationExpiryIndex);
        });

        modelBuilder.Entity<ReservationLine>(line =>
        {
            line.ToTable("ReservationLines", table =>
            {
                table.HasCheckConstraint("CK_ReservationLines_Quantity", $"[Quantity] >= 1 AND [Quantity] <= {Quantities.MaxLineQuantity}");
                table.HasTrigger(ReservationLineImmutableTrigger);
            });
            line.HasKey(l => new { l.ReservationId, l.InventoryItemId });
            line.HasOne<InventoryItem>().WithMany().HasForeignKey(l => l.InventoryItemId).OnDelete(DeleteBehavior.Restrict);
            line.HasIndex(l => l.InventoryItemId).HasDatabaseName("IX_ReservationLines_InventoryItemId");
        });
    }

    private static string AllowedValues<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"[{column}] IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(name => $"N'{name}'"))})";
}
