using EnterpriseCommerce.Cart.Infrastructure.Persistence;
using EnterpriseCommerce.Cart.Infrastructure.Persistence.Migrations;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EnterpriseCommerce.Cart.IntegrationTests.Persistence;

/// <summary>The migrations (CART_OPERATIONS.md section 6): from an empty database, the model match, and the rollback rule.</summary>
public sealed class MigrationTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    [Fact]
    public async Task EveryMigration_AppliesToAnEmptyDatabase_AndTheModelHasNoPendingChanges()
    {
        database.RequireAvailable();
        await using var context = database.CreateContext();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(context.Database.GetMigrations(), await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(context.Database.HasPendingModelChanges(), "The model has changes that no migration captures.");
    }

    [Fact]
    public async Task TheSchema_HasTheNamedIndexesAndConstraints()
    {
        database.RequireAvailable();

        foreach (var index in new[] { CartDbContext.CustomerIndex, CartDbContext.UpdatedAtIndex, CartDbContext.LineIndex })
        {
            Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.indexes WHERE name = '{index}'"));
        }

        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM sys.indexes WHERE name = '{CartDbContext.CustomerIndex}' AND is_unique = 1"));
        Assert.Equal(10, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id IN (OBJECT_ID('Carts'), OBJECT_ID('CartItems'))"));
        Assert.Equal(1, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE name = 'FK_CartItems_Carts_CartId' AND delete_referential_action = 1"));
        Assert.True(await database.ScalarAsync<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
    }

    [Fact]
    public async Task RollingBack_IsRefused_WhileCartsExist_AndAllowedOnAnEmptyDatabase()
    {
        database.RequireAvailable();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();

        var subject = $"customer-{Guid.NewGuid():N}";
        await database.ExecuteAsync($"INSERT INTO Carts (Id, CustomerSubject, Version, CreatedAt, UpdatedAt) VALUES (NEWID(), N'{subject}', 1, SYSUTCDATETIME(), SYSUTCDATETIME())");

        var refused = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(Migration.InitialDatabase, TestContext.Current.CancellationToken));
        Assert.Equal(InitialCartSchema.CartDataExistsError, refused.Number);
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject = N'{subject}'"));

        await database.ExecuteAsync("DELETE FROM Carts");
        await migrator.MigrateAsync(Migration.InitialDatabase, TestContext.Current.CancellationToken);
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Carts', 'CartItems')"));

        // Forward again, so the fixture's database is usable by the other tests of this class.
        await migrator.MigrateAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Carts', 'CartItems')"));
    }
}
