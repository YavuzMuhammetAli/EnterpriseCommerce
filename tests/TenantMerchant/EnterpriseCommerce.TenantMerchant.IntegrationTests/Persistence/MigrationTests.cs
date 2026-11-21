using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Persistence;

/// <summary>The migrations build the expected schema on an empty SQL Server database and match the model.</summary>
public sealed class MigrationTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    [Fact]
    public async Task Migrations_FromAnEmptyDatabase_AreCompleteAndMatchTheModel()
    {
        database.RequireAvailable();
        await using var context = database.CreateContext();
        var ct = TestContext.Current.CancellationToken;

        Assert.Contains(await context.Database.GetAppliedMigrationsAsync(ct), migration => migration.EndsWith("_InitialTenantMerchantSchema", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
        Assert.False(context.Database.HasPendingModelChanges(), "The model has changes that no migration captures.");
    }

    [Fact]
    public async Task Schema_HasTheConcurrencyInvariantsAsConstraints()
    {
        database.RequireAvailable();

        var indexes = await database.ScalarAsync<string>("""
            SELECT STRING_AGG(CONCAT(t.name, '.', i.name, ':', i.is_unique, ':', ISNULL(i.filter_definition, '-')), '|') WITHIN GROUP (ORDER BY t.name, i.name)
            FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
            WHERE i.name LIKE 'UX[_]%' OR i.name LIKE 'IX[_]%'
            """);
        Assert.Equal(
            "Memberships.IX_Memberships_TenantId_CreatedAt:0:-"
            + "|Memberships.UX_Memberships_TenantId_SubjectId_Current:1:([Status]<>N'Removed')"
            + "|Tenants.UX_Tenants_ProvisionedByClientId_ProvisioningKey:1:-"
            + "|Tenants.UX_Tenants_Slug:1:-",
            indexes);

        var checks = await database.ScalarAsync<string>(
            "SELECT STRING_AGG(name, '|') WITHIN GROUP (ORDER BY name) FROM sys.check_constraints");
        Assert.Equal("CK_Memberships_Role|CK_Memberships_Status|CK_Tenants_Status", checks);

        var foreignKeys = await database.ScalarAsync<string>(
            "SELECT STRING_AGG(CONCAT(OBJECT_NAME(parent_object_id), '->', OBJECT_NAME(referenced_object_id)), '|') WITHIN GROUP (ORDER BY name) FROM sys.foreign_keys");
        Assert.Equal("Memberships->Tenants|MerchantProfiles->Tenants", foreignKeys);

        var rowVersions = await database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE ty.name = 'timestamp' AND c.name = 'RowVersion'");
        Assert.Equal(3, rowVersions);
    }

    [Fact]
    public async Task Migrations_CanBeRolledBackAndReapplied()
    {
        database.RequireAvailable();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var ct = TestContext.Current.CancellationToken;

        await migrator.MigrateAsync(Migration.InitialDatabase, cancellationToken: ct);
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Tenants', 'MerchantProfiles', 'Memberships')"));

        await context.Database.MigrateAsync(ct);
        Assert.Equal(3, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Tenants', 'MerchantProfiles', 'Memberships')"));
    }
}
