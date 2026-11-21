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

        var applied = await context.Database.GetAppliedMigrationsAsync(ct);
        Assert.Contains(applied, migration => migration.EndsWith("_InitialTenantMerchantSchema", StringComparison.Ordinal));
        Assert.Contains(applied, migration => migration.EndsWith("_MerchantMembershipAdministration", StringComparison.Ordinal));
        Assert.Contains(applied, migration => migration.EndsWith("_SelfServiceOnboarding", StringComparison.Ordinal));
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
            "MembershipAuditRecords.IX_MembershipAuditRecords_TenantId_OccurredAt:0:-"
            + "|Memberships.IX_Memberships_SubjectId_Status:0:-"
            + "|Memberships.IX_Memberships_TenantId_ActiveOwner:0:([Role]=N'MerchantOwner' AND [Status]=N'Active')"
            + "|Memberships.IX_Memberships_TenantId_CreatedAt:0:-"
            + "|Memberships.UX_Memberships_TenantId_SubjectId_Current:1:([Status]<>N'Removed')"
            + "|MerchantInvitations.IX_MerchantInvitations_TenantId_CreatedAt:0:-"
            + "|MerchantInvitations.UX_MerchantInvitations_TenantId_NormalizedEmail_Pending:1:([Status]=N'Pending')"
            + "|MerchantInvitations.UX_MerchantInvitations_TokenHash:1:-"
            + "|Tenants.UX_Tenants_ProvisionedByClientId_ProvisioningKey:1:([Origin]=N'Provisioned')"
            + "|Tenants.UX_Tenants_SelfServiceSubjectId_ProvisioningKey:1:([Origin]=N'SelfService')"
            + "|Tenants.UX_Tenants_Slug:1:-",
            indexes);

        var checks = await database.ScalarAsync<string>(
            "SELECT STRING_AGG(name, '|') WITHIN GROUP (ORDER BY name) FROM sys.check_constraints");
        Assert.Equal(
            "CK_MembershipAuditRecords_Action|CK_MembershipAuditRecords_ActorKind|CK_Memberships_Role|CK_Memberships_Status"
            + "|CK_MerchantInvitations_Accepted|CK_MerchantInvitations_Cancelled|CK_MerchantInvitations_Expired|CK_MerchantInvitations_Expiry"
            + "|CK_MerchantInvitations_Role|CK_MerchantInvitations_Status|CK_Tenants_Origin|CK_Tenants_Origin_SelfServiceSubject|CK_Tenants_Status",
            checks);

        var foreignKeys = await database.ScalarAsync<string>(
            "SELECT STRING_AGG(CONCAT(OBJECT_NAME(parent_object_id), '->', OBJECT_NAME(referenced_object_id)), '|') WITHIN GROUP (ORDER BY name) FROM sys.foreign_keys");
        Assert.Equal("MembershipAuditRecords->Tenants|Memberships->Tenants|MerchantInvitations->Tenants|MerchantProfiles->Tenants", foreignKeys);

        // Every mutable table; the append-only audit table has none (ADR-021).
        var rowVersions = await database.ScalarAsync<string>(
            "SELECT STRING_AGG(OBJECT_NAME(c.object_id), '|') WITHIN GROUP (ORDER BY OBJECT_NAME(c.object_id)) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE ty.name = 'timestamp' AND c.name = 'RowVersion'");
        Assert.Equal("Memberships|MerchantInvitations|MerchantProfiles|Tenants", rowVersions);

        // The backstops of ADR-021: the last-owner trigger runs after the change, the audit trigger replaces it.
        var triggers = await database.ScalarAsync<string>(
            "SELECT STRING_AGG(CONCAT(OBJECT_NAME(parent_id), '.', name, ':', is_instead_of_trigger, ':', is_disabled), '|') WITHIN GROUP (ORDER BY name) FROM sys.triggers");
        Assert.Equal("MembershipAuditRecords.TR_MembershipAuditRecords_AppendOnly:1:0|Memberships.TR_Memberships_RequireActiveOwner:0:0", triggers);
    }

    [Fact]
    public async Task Database_UsesReadCommittedSnapshot_WhichTheWriteGateAndTriggerTolerate()
    {
        // EF Core creates the database with READ_COMMITTED_SNAPSHOT on. The tenant write gate does not depend on it
        // (its UPDLOCK serializes writers either way) and the last-owner trigger forces a locking read
        // (READCOMMITTEDLOCK). This test records the setting the concurrency tests ran under.
        database.RequireAvailable();
        Assert.True(await database.ScalarAsync<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
    }

    [Fact]
    public async Task Migrations_CanBeRolledBackAndReapplied()
    {
        database.RequireAvailable();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var ct = TestContext.Current.CancellationToken;

        const string s2Objects =
            "SELECT (SELECT COUNT(*) FROM sys.objects WHERE name IN ('MerchantInvitations', 'MembershipAuditRecords', 'TR_Memberships_RequireActiveOwner', 'TR_MembershipAuditRecords_AppendOnly'))"
            + " + (SELECT COUNT(*) FROM sys.indexes WHERE name IN ('IX_Memberships_TenantId_ActiveOwner', 'IX_Memberships_SubjectId_Status'))";

        // S3 down to S2: the origin columns, the self-service index and constraints go, and the S2 key index returns.
        await migrator.MigrateAsync("MerchantMembershipAdministration", cancellationToken: ct);
        Assert.Equal(0, await database.ScalarAsync<int>(S3Objects));
        Assert.Equal("-", await database.ScalarAsync<string>(ProvisioningKeyFilter));
        Assert.Equal(6, await database.ScalarAsync<int>(s2Objects));

        // S2 down to S1: its tables, triggers and indexes go, and the S1 schema stays.
        await migrator.MigrateAsync("InitialTenantMerchantSchema", cancellationToken: ct);
        Assert.Equal(0, await database.ScalarAsync<int>(s2Objects));
        Assert.Equal(3, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Tenants', 'MerchantProfiles', 'Memberships')"));

        await migrator.MigrateAsync(Migration.InitialDatabase, cancellationToken: ct);
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Tenants', 'MerchantProfiles', 'Memberships')"));

        await context.Database.MigrateAsync(ct);
        Assert.Equal(6, await database.ScalarAsync<int>(s2Objects));
        Assert.Equal(5, await database.ScalarAsync<int>(S3Objects));
        Assert.Equal("([Origin]=N'Provisioned')", await database.ScalarAsync<string>(ProvisioningKeyFilter));
        Assert.Equal(5, await database.ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('Tenants', 'MerchantProfiles', 'Memberships', 'MerchantInvitations', 'MembershipAuditRecords')"));
    }

    [Fact]
    public async Task SelfServiceMigration_KeepsExistingTenants_AsProvisioned()
    {
        database.RequireAvailable();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var ct = TestContext.Current.CancellationToken;
        var tenantId = Guid.CreateVersion7();

        await migrator.MigrateAsync("MerchantMembershipAdministration", cancellationToken: ct);
        try
        {
            // A tenant written by S2, before the origin existed.
            await database.ExecuteAsync(
                "INSERT INTO Tenants (Id, Slug, Status, CreatedAt, ProvisionedByClientId, ProvisioningKey, ProvisioningRequestHash, ProvisionedByActor)"
                + $" VALUES ('{tenantId}', 'pre-s3-{tenantId:N}', N'Active', SYSDATETIMEOFFSET(), N'svc-admin', N'pre-s3-{tenantId:N}', {EmptyHash}, N'admin:1');"
                + $" INSERT INTO MerchantProfiles (TenantId, DisplayName, CreatedAt, UpdatedAt) VALUES ('{tenantId}', N'Pre S3', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());");

            await context.Database.MigrateAsync(ct);

            Assert.Equal("Provisioned", await database.ScalarAsync<string>($"SELECT Origin FROM Tenants WHERE Id = '{tenantId}'"));
            Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Tenants WHERE Id = '{tenantId}' AND SelfServiceSubjectId IS NOT NULL"));
        }
        finally
        {
            await context.Database.MigrateAsync(ct);
            await database.ExecuteAsync($"DELETE FROM MerchantProfiles WHERE TenantId = '{tenantId}'; DELETE FROM Tenants WHERE Id = '{tenantId}';");
        }
    }

    [Fact]
    public async Task SelfServiceMigration_RefusesToRollBack_WhenSelfServiceTenantsExist()
    {
        database.RequireAvailable();
        await using var context = database.CreateContext();
        var migrator = context.GetService<IMigrator>();
        var ct = TestContext.Current.CancellationToken;
        var tenantId = Guid.CreateVersion7();

        await database.ExecuteAsync(
            "INSERT INTO Tenants (Id, Slug, Status, CreatedAt, Origin, SelfServiceSubjectId, ProvisionedByClientId, ProvisioningKey, ProvisioningRequestHash, ProvisionedByActor)"
            + $" VALUES ('{tenantId}', 'self-{tenantId:N}', N'Active', SYSDATETIMEOFFSET(), 'SelfService', N'user-1', N'bff-merchant', N'k-{tenantId:N}', {EmptyHash}, N'user-1');"
            + $" INSERT INTO MerchantProfiles (TenantId, DisplayName, CreatedAt, UpdatedAt) VALUES ('{tenantId}', N'Self', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());");
        try
        {
            var refusal = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
                () => migrator.MigrateAsync("MerchantMembershipAdministration", cancellationToken: ct));

            Assert.Equal(EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence.Migrations.SelfServiceOnboarding.SelfServiceDataExistsError, refusal.Number);
            Assert.Equal(5, await database.ScalarAsync<int>(S3Objects));
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(ct));
        }
        finally
        {
            await database.ExecuteAsync($"DELETE FROM MerchantProfiles WHERE TenantId = '{tenantId}'; DELETE FROM Tenants WHERE Id = '{tenantId}';");
        }
    }

    private const string S3Objects =
        "SELECT (SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('Tenants') AND name IN ('Origin', 'SelfServiceSubjectId'))"
        + " + (SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_Tenants_SelfServiceSubjectId_ProvisioningKey')"
        + " + (SELECT COUNT(*) FROM sys.check_constraints WHERE name IN ('CK_Tenants_Origin', 'CK_Tenants_Origin_SelfServiceSubject'))";

    private const string ProvisioningKeyFilter =
        "SELECT ISNULL(filter_definition, '-') FROM sys.indexes WHERE name = 'UX_Tenants_ProvisionedByClientId_ProvisioningKey'";

    /// <summary>A 32-byte zero hash literal.</summary>
    private const string EmptyHash = "0x0000000000000000000000000000000000000000000000000000000000000000";
}
