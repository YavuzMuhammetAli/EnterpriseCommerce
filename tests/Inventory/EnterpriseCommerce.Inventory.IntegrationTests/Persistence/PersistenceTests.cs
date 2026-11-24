using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Infrastructure.Persistence;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Inventory.UnitTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Persistence;

/// <summary>
/// The persistence guarantees of ADR-026 on a real SQL Server: a balance, its movement and its outbox rows commit
/// together or not at all; the constraints and triggers refuse what the domain never does; and concurrent writers of one
/// item are serialized by its row lock.
/// </summary>
public sealed class PersistenceTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private const int CheckViolation = 547;

    [Fact]
    public async Task AnItem_ItsInitialMovement_AndItsEvent_CommitTogether()
    {
        database.RequireAvailable();
        var tenant = Guid.CreateVersion7();

        var item = await InventorySeed.CreateItemAsync(database, tenant, onHand: 9);

        Assert.Equal("9|0|1", await database.ScalarAsync<string>($"SELECT CONCAT(OnHand, '|', Reserved, '|', Version) FROM InventoryItems WHERE Id = '{item.Id}'"));
        Assert.Equal("InitialStock|0|9|1|User|merchant-owner-1", await database.ScalarAsync<string>($"SELECT CONCAT(Type, '|', OnHandBefore, '|', OnHandAfter, '|', ItemVersion, '|', ActorKind, '|', ActorId) FROM StockMovements WHERE InventoryItemId = '{item.Id}'"));
        var envelope = await database.ScalarAsync<string>($"SELECT Envelope FROM OutboxMessages WHERE MessageKey = '{tenant}'");
        EventSchemas.AssertValid(envelope);
    }

    [Fact]
    public async Task ARolledBackWrite_LeavesNoBalanceMovementOrEvent()
    {
        database.RequireAvailable();
        var item = InventorySeed.NewItem(Guid.CreateVersion7(), onHand: 4);

        await database.Gate().ExecuteAsync(
            "test-rollback",
            (session, _) =>
            {
                session.UseMessageMetadata(InventorySeed.Metadata);
                session.Add(item);
                return Task.FromResult(WriteDecision.Rollback(0));
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountForItemAsync(item.Id));
    }

    [Fact]
    public async Task AWriteThatFailsInTheDatabase_CommitsNeitherTheBalanceNorItsMovementNorItsEvent()
    {
        database.RequireAvailable();
        var tenant = Guid.CreateVersion7();
        var existing = await InventorySeed.CreateItemAsync(database, tenant, onHand: 10);
        var before = await SnapshotAsync(existing.Id);
        var outboxBefore = await database.ScalarAsync<int>("SELECT COUNT(*) FROM OutboxMessages");

        // An adjustment and, in the same transaction, a second item for the same variant: the unique index refuses the
        // save, and the adjustment's balance, movement and event roll back with it.
        var failure = await Assert.ThrowsAsync<InventoryWriteConflictException>(() => database.Gate().ExecuteAsync(
            "test-failing-save",
            async (session, cancellationToken) =>
            {
                session.UseMessageMetadata(InventorySeed.Metadata);
                var item = (await session.LockItemAsync(tenant, existing.Id, cancellationToken))!;
                item.Adjust(5, AdjustmentReason.Receipt, Actor.User(InventorySeed.Subject), InventorySeed.Key(), InventorySeed.Correlation, session.Now);
                session.Add(InventorySeed.NewItem(tenant, onHand: 1, variantId: existing.VariantId));
                return WriteDecision.Commit(0);
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(InventoryWriteConflict.UniqueConstraint, failure.Conflict);
        Assert.Equal(before, await SnapshotAsync(existing.Id));
        Assert.Equal(outboxBefore, await database.ScalarAsync<int>("SELECT COUNT(*) FROM OutboxMessages"));
    }

    [Fact]
    public async Task OneTenantHoldsOneBalancePerVariant_WhileAnotherTenantMayTrackTheSameVariant()
    {
        database.RequireAvailable();
        var variant = Guid.CreateVersion7();
        var tenant = Guid.CreateVersion7();
        await InventorySeed.CreateItemAsync(database, tenant, onHand: 1, variantId: variant);

        var failure = await Assert.ThrowsAsync<InventoryWriteConflictException>(() => InventorySeed.CreateItemAsync(database, tenant, onHand: 2, variantId: variant));
        await InventorySeed.CreateItemAsync(database, Guid.CreateVersion7(), onHand: 2, variantId: variant);

        Assert.Equal(InventoryWriteConflict.UniqueConstraint, failure.Conflict);
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM InventoryItems WHERE TenantId = '{tenant}' AND VariantId = '{variant}'"));
    }

    [Theory]
    [InlineData("SET OnHand = -1")]
    [InlineData("SET Reserved = -1")]
    [InlineData("SET Reserved = OnHand + 1")]
    [InlineData("SET OnHand = 1000000001")]
    [InlineData("SET Version = 0")]
    public async Task TheDatabase_RefusesABrokenBalance_ThatBypassesTheDomain(string assignment)
    {
        database.RequireAvailable();
        var item = await InventorySeed.CreateItemAsync(database, Guid.CreateVersion7(), onHand: 5);

        var failure = await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync($"UPDATE InventoryItems {assignment} WHERE Id = '{item.Id}'"));

        Assert.Equal(CheckViolation, failure.Number);
    }

    [Fact]
    public async Task TheLedger_IsLinear_AndAppendOnly()
    {
        database.RequireAvailable();
        var item = await InventorySeed.CreateItemAsync(database, Guid.CreateVersion7(), onHand: 5);
        var movement = await database.ScalarAsync<Guid>($"SELECT Id FROM StockMovements WHERE InventoryItemId = '{item.Id}'");
        string Insert(long version, int before, int delta, int after) =>
            "INSERT INTO StockMovements (Id, TenantId, InventoryItemId, ItemVersion, Type, Reason, OnHandDelta, ReservedDelta, OnHandBefore, ReservedBefore, OnHandAfter, ReservedAfter, ActorKind, ActorId, CorrelationId, OccurredAt, IdempotencyKey, RequestHash) "
            + $"VALUES (NEWID(), '{item.TenantId}', '{item.Id}', {version}, 'Adjustment', 'Correction', {delta}, 0, {before}, 0, {after}, 0, 'User', 'u', 'c', SYSDATETIMEOFFSET(), CONVERT(nvarchar(36), NEWID()), 0x{new string('0', 64)})";

        // after != before + delta, a second movement for one version, an update and a delete: all refused.
        Assert.Equal(CheckViolation, (await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync(Insert(2, 5, 1, 7)))).Number);
        Assert.Equal(SqlServerErrors.UniqueIndexViolation, (await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync(Insert(1, 5, 1, 6)))).Number);
        Assert.Equal(SqlServerErrors.MovementAppendOnlyViolation, (await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync($"UPDATE StockMovements SET OnHandDelta = 6 WHERE Id = '{movement}'"))).Number);
        Assert.Equal(SqlServerErrors.MovementAppendOnlyViolation, (await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync($"DELETE FROM StockMovements WHERE Id = '{movement}'"))).Number);
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM StockMovements WHERE InventoryItemId = '{item.Id}'"));
    }

    [Fact]
    public async Task ASaveWithEvents_ButNoMessageMetadata_FailsClosed()
    {
        database.RequireAvailable();
        var item = InventorySeed.NewItem(Guid.CreateVersion7(), onHand: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => database.Gate().ExecuteAsync(
            "test-no-metadata",
            (session, _) =>
            {
                session.Add(item);
                return Task.FromResult(WriteDecision.Commit(0));
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(0, await CountForItemAsync(item.Id));
    }

    [Fact]
    public async Task AnItemChangedWithoutAMovement_IsNeverSaved()
    {
        database.RequireAvailable();
        var item = await InventorySeed.CreateItemAsync(database, Guid.CreateVersion7(), onHand: 1);
        await using var context = database.CreateContext();
        var loaded = await context.InventoryItems.SingleAsync(i => i.Id == item.Id, TestContext.Current.CancellationToken);

        context.Entry(loaded).Property(i => i.Version).CurrentValue = 2;
        context.Entry(loaded).Property(i => i.OnHand).CurrentValue = 100;

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT OnHand FROM InventoryItems WHERE Id = '{item.Id}'"));
    }

    [Fact]
    public async Task ConcurrentAdjustmentsOfOneItem_AreSerialized_AndEveryOneIsRecordedOnce()
    {
        database.RequireAvailable();
        const int Writers = 16;
        const int Rounds = 5;
        for (var round = 0; round < Rounds; round++)
        {
            var tenant = Guid.CreateVersion7();
            var item = await InventorySeed.CreateItemAsync(database, tenant, onHand: 100);
            using var start = new Barrier(Writers);

            // Half receive one unit, half lose one: whatever the interleaving, every change applies once, in order.
            var changes = await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(30));
                return await InventorySeed.AdjustAsync(database, tenant, item.Id, writer % 2 == 0 ? 1 : -1, writer % 2 == 0 ? AdjustmentReason.Receipt : AdjustmentReason.Loss);
            })));

            Assert.All(changes, change => Assert.True(change.Succeeded));
            Assert.Equal($"100|{Writers + 1}", await database.ScalarAsync<string>($"SELECT CONCAT(OnHand, '|', Version) FROM InventoryItems WHERE Id = '{item.Id}'"));
            Assert.Equal(
                string.Join(",", Enumerable.Range(1, Writers + 1)),
                await database.ScalarAsync<string>($"SELECT STRING_AGG(CAST(ItemVersion AS varchar(10)), ',') WITHIN GROUP (ORDER BY ItemVersion) FROM StockMovements WHERE InventoryItemId = '{item.Id}'"));

            // Each movement starts where the previous one ended: the ledger replays to the balance.
            Assert.Equal(0, await database.ScalarAsync<int>(
                $"SELECT COUNT(*) FROM StockMovements m JOIN StockMovements p ON p.InventoryItemId = m.InventoryItemId AND p.ItemVersion = m.ItemVersion - 1 WHERE m.InventoryItemId = '{item.Id}' AND (m.OnHandBefore <> p.OnHandAfter OR m.ReservedBefore <> p.ReservedAfter)"));
            Assert.Equal(Writers + 1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE MessageKey = '{tenant}'"));
        }
    }

    [Fact]
    public async Task ConcurrentCreationsOfOneVariant_CreateExactlyOneItem()
    {
        database.RequireAvailable();
        const int Creators = 8;
        var tenant = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        using var start = new Barrier(Creators);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, Creators).Select(_ => Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(30));
            try
            {
                await InventorySeed.CreateItemAsync(database, tenant, onHand: 3, variantId: variant);
                return "created";
            }
            catch (InventoryWriteConflictException exception) when (exception.Conflict == InventoryWriteConflict.UniqueConstraint)
            {
                return "conflict";
            }
        })));

        Assert.Single(outcomes, outcome => outcome == "created");
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM InventoryItems WHERE TenantId = '{tenant}' AND VariantId = '{variant}'"));
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE MessageKey = '{tenant}'"));
    }

    private async Task<string> SnapshotAsync(Guid itemId) =>
        await database.ScalarAsync<string>(
            $"SELECT CONCAT(i.OnHand, '|', i.Reserved, '|', i.Version, '|', (SELECT COUNT(*) FROM StockMovements m WHERE m.InventoryItemId = i.Id)) FROM InventoryItems i WHERE i.Id = '{itemId}'");

    private async Task<int> CountForItemAsync(Guid itemId) =>
        await database.ScalarAsync<int>($"SELECT (SELECT COUNT(*) FROM InventoryItems WHERE Id = '{itemId}') + (SELECT COUNT(*) FROM StockMovements WHERE InventoryItemId = '{itemId}') + (SELECT COUNT(*) FROM OutboxMessages WHERE Envelope LIKE '%{itemId}%')");
}
