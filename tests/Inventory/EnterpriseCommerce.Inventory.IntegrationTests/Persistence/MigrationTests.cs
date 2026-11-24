using EnterpriseCommerce.Inventory.Infrastructure.Persistence;
using EnterpriseCommerce.Inventory.Infrastructure.Persistence.Migrations;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Persistence;

/// <summary>
/// The migration on a real SQL Server (ADR-026): from an empty database to the designed schema, with every invariant a
/// database constraint, and a rollback that never discards stock, history or unpublished events.
/// </summary>
public sealed class MigrationTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    [Fact]
    public async Task FromAnEmptyDatabase_TheMigrationAppliesTheModel_WithNoPendingChanges()
    {
        database.RequireAvailable();
        await using var context = database.CreateContext();

        var applied = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["InitialInventorySchema"], applied.Select(name => name[(name.IndexOf('_') + 1)..]));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task TheSchema_HasEveryDesignedConstraintIndexAndTrigger()
    {
        database.RequireAvailable();

        var checks = await database.ScalarAsync<string>("SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY name) FROM sys.check_constraints");
        Assert.Equal(
            "CK_InventoryItems_OnHand,CK_InventoryItems_Reserved,CK_InventoryItems_Version,CK_OutboxMessages_Attempts,CK_OutboxMessages_EnvelopeSize,CK_OutboxMessages_EventVersion,"
            + "CK_ReservationLines_Quantity,CK_Reservations_Completion,CK_Reservations_Expiry,CK_Reservations_Status,CK_Reservations_Version,"
            + "CK_StockMovements_ActorKind,CK_StockMovements_Balance,CK_StockMovements_ItemVersion,CK_StockMovements_Reason,CK_StockMovements_Shape,CK_StockMovements_Type",
            checks);

        var unique = await database.ScalarAsync<string>("SELECT STRING_AGG(i.name, ',') WITHIN GROUP (ORDER BY i.name) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id WHERE t.is_ms_shipped = 0 AND i.is_unique = 1 AND i.is_primary_key = 0");
        Assert.Equal(
            $"{InventoryDbContext.ItemCreationKeyIndex},{InventoryDbContext.ItemVariantIndex},UX_OutboxMessages_EventId_EventVersion,{InventoryDbContext.ReservationKeyIndex},"
            + $"{InventoryDbContext.MovementVersionIndex},{InventoryDbContext.AdjustmentKeyIndex}",
            unique);

        Assert.Equal(
            "[ExpiresAt]|([Status]='Active')",
            await database.ScalarAsync<string>(
                $"SELECT CONCAT('[', c.name, ']|', i.filter_definition) FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE i.name = '{InventoryDbContext.ActiveReservationExpiryIndex}'"));

        Assert.Equal(
            $"{InventoryDbContext.ReservationLineImmutableTrigger},{InventoryDbContext.MovementAppendOnlyTrigger}",
            await database.ScalarAsync<string>("SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY name) FROM sys.triggers"));
        Assert.Equal(
            "InventoryItems.RowVersion,Reservations.RowVersion",
            await database.ScalarAsync<string>("SELECT STRING_AGG(CONCAT(OBJECT_NAME(object_id), '.', name), ',') WITHIN GROUP (ORDER BY OBJECT_NAME(object_id)) FROM sys.columns WHERE system_type_id = 189"));

        // No foreign key leaves the service's own tables (database per service, ADR-003).
        Assert.Equal(
            "InventoryItems,Reservations",
            await database.ScalarAsync<string>("SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY name) FROM (SELECT DISTINCT OBJECT_NAME(referenced_object_id) AS name FROM sys.foreign_keys) AS referenced"));
    }

    [Fact]
    public async Task TheDatabase_UsesReadCommittedSnapshot_AndTheLockingReadsDoNotDependOnIt()
    {
        // EF Core creates the database with READ_COMMITTED_SNAPSHOT on. Every decision rests on locking reads (UPDLOCK),
        // which read the latest committed row either way (ADR-026 section 9); the concurrency tests run on this setting.
        database.RequireAvailable();

        Assert.True(await database.ScalarAsync<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
    }

    [Fact]
    public async Task RollingBack_AnEmptyDatabase_RemovesTheSchema_AndTheMigrationRestoresIt()
    {
        await using var empty = new SqlServerTestDatabase();
        await empty.InitializeAsync();
        empty.RequireAvailable();
        await using var context = empty.CreateContext();
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync(Migration.InitialDatabase, TestContext.Current.CancellationToken);
        Assert.Equal(0, await empty.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('InventoryItems', 'StockMovements', 'Reservations', 'ReservationLines', 'OutboxMessages')"));

        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(5, await empty.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('InventoryItems', 'StockMovements', 'Reservations', 'ReservationLines', 'OutboxMessages')"));
    }

    [Fact]
    public async Task RollingBack_ADatabaseWithStock_IsRefused_AndKeepsEverything()
    {
        await using var stocked = new SqlServerTestDatabase();
        await stocked.InitializeAsync();
        stocked.RequireAvailable();
        await InventorySeed.CreateItemAsync(stocked, Guid.CreateVersion7(), onHand: 3);
        await using var context = stocked.CreateContext();

        var failure = await Assert.ThrowsAsync<SqlException>(() => context.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase, TestContext.Current.CancellationToken));

        Assert.Equal(InitialInventorySchema.InventoryDataExistsError, failure.Number);
        Assert.Equal((1, 1, 1), (
            await stocked.ScalarAsync<int>("SELECT COUNT(*) FROM InventoryItems"),
            await stocked.ScalarAsync<int>("SELECT COUNT(*) FROM StockMovements"),
            await stocked.ScalarAsync<int>("SELECT COUNT(*) FROM OutboxMessages")));
    }
}
