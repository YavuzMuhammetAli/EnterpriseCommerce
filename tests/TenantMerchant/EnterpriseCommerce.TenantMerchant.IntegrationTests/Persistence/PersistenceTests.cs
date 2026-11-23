using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Persistence;

/// <summary>
/// Transactional and constraint behaviour on a real SQL Server: atomic provisioning, rollback, uniqueness, allowed
/// values and optimistic concurrency.
/// </summary>
public sealed class PersistenceTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly MessageMetadata Metadata = MessageMetadata.ForRequest("persistence-test")!;

    [Fact]
    public async Task AddAsync_CommitsTheTenantProfileAndOwnerTogether()
    {
        database.RequireAvailable();
        var (tenant, owner) = NewTenant("atomic");

        await using (var context = database.CreateContext())
        {
            Assert.Equal(TenantInsertOutcome.Inserted, await new TenantProvisioningStore(context).AddAsync(tenant, owner, AuditOf(owner), Metadata, TestContext.Current.CancellationToken));
        }

        await using var verify = database.CreateContext();
        var stored = await verify.Tenants.SingleAsync(t => t.Id == tenant.Id, TestContext.Current.CancellationToken);
        Assert.Equal("atomic", stored.Slug);
        Assert.Equal("atomic store", stored.MerchantProfile.DisplayName);
        Assert.Equal(TimeSpan.Zero, stored.CreatedAt.Offset);
        var membership = await verify.Memberships.SingleAsync(m => m.TenantId == tenant.Id, TestContext.Current.CancellationToken);
        Assert.Equal(MerchantRole.MerchantOwner, membership.Role);
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM MerchantProfiles WHERE TenantId = '{tenant.Id}'"));
    }

    [Fact]
    public async Task AddAsync_WritesTheTenantCreatedRow_InTheSameSave()
    {
        database.RequireAvailable();
        var (tenant, owner) = NewTenant("with-event");

        await using (var context = database.CreateContext())
        {
            Assert.Equal(TenantInsertOutcome.Inserted, await new TenantProvisioningStore(context).AddAsync(tenant, owner, AuditOf(owner), Metadata, TestContext.Current.CancellationToken));
        }

        var row = Assert.Single(await OutboxRows.TenantCreatedAsync(database, tenant.Id));
        Assert.Contains("\"correlationId\":\"persistence-test\"", row.Envelope, StringComparison.Ordinal);
        Assert.Empty(tenant.DomainEvents);
    }

    [Fact]
    public async Task AddAsync_WithoutMessageMetadata_FailsClosed_AndWritesNothing()
    {
        // ADR-023 section 5: saving an aggregate with public domain events and no metadata fails closed.
        database.RequireAvailable();
        var (tenant, owner) = NewTenant("no-metadata");

        await using (var context = database.CreateContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new TenantProvisioningStore(context).AddAsync(tenant, owner, AuditOf(owner), metadata: null, TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Tenants WHERE Id = '{tenant.Id}'"));
        Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE TenantId = '{tenant.Id}'"));
        Assert.Empty(await OutboxRows.TenantCreatedAsync(database, tenant.Id));
    }

    [Fact]
    public async Task AddAsync_RollsBackEverythingWhenTheMembershipFails()
    {
        database.RequireAvailable();

        // Test-only failure injection: SQL Server itself rejects the owner membership insert.
        await database.ExecuteAsync("ALTER TABLE Memberships ADD CONSTRAINT CK_Test_RejectSubject CHECK (SubjectId <> N'reject-me')");
        try
        {
            var (tenant, owner) = NewTenant("rollback", ownerSubject: "reject-me");

            await using (var context = database.CreateContext())
            {
                var failure = await Assert.ThrowsAsync<DataStoreUnavailableException>(
                    () => new TenantProvisioningStore(context).AddAsync(tenant, owner, AuditOf(owner), Metadata, TestContext.Current.CancellationToken));
                Assert.Contains("CK_Test_RejectSubject", failure.InnerException!.ToString(), StringComparison.Ordinal);
                Assert.Equal(547, Assert.IsType<SqlException>(failure.InnerException.InnerException).Number);
            }

            Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Tenants WHERE Id = '{tenant.Id}'"));
            Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM MerchantProfiles WHERE TenantId = '{tenant.Id}'"));
            Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE TenantId = '{tenant.Id}'"));
        }
        finally
        {
            await database.ExecuteAsync("ALTER TABLE Memberships DROP CONSTRAINT CK_Test_RejectSubject");
        }
    }

    [Fact]
    public async Task AddAsync_ReportsADuplicateSlugAsAConflictAndWritesNothing()
    {
        database.RequireAvailable();
        var (first, firstOwner) = NewTenant("duplicate-slug");
        var (second, secondOwner) = NewTenant("duplicate-slug", key: "other-key");

        await using var context = database.CreateContext();
        var store = new TenantProvisioningStore(context);
        Assert.Equal(TenantInsertOutcome.Inserted, await store.AddAsync(first, firstOwner, AuditOf(firstOwner), Metadata, TestContext.Current.CancellationToken));
        Assert.Equal(TenantInsertOutcome.UniqueConflict, await store.AddAsync(second, secondOwner, AuditOf(secondOwner), Metadata, TestContext.Current.CancellationToken));

        Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE TenantId = '{second.Id}'"));
        Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM MerchantProfiles WHERE TenantId = '{second.Id}'"));
    }

    [Fact]
    public async Task AddAsync_ReportsADuplicateProvisioningKeyAsAConflict()
    {
        database.RequireAvailable();
        var (first, firstOwner) = NewTenant("key-one", key: "shared-key");
        var (second, secondOwner) = NewTenant("key-two", key: "shared-key");
        var (otherClient, otherOwner) = NewTenant("key-three", key: "shared-key", client: "svc-other");

        await using var context = database.CreateContext();
        var store = new TenantProvisioningStore(context);
        Assert.Equal(TenantInsertOutcome.Inserted, await store.AddAsync(first, firstOwner, AuditOf(firstOwner), Metadata, TestContext.Current.CancellationToken));
        Assert.Equal(TenantInsertOutcome.UniqueConflict, await store.AddAsync(second, secondOwner, AuditOf(secondOwner), Metadata, TestContext.Current.CancellationToken));

        // Keys are unique per client only.
        Assert.Equal(TenantInsertOutcome.Inserted, await store.AddAsync(otherClient, otherOwner, AuditOf(otherOwner), Metadata, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Memberships_AreUniquePerSubjectAndTenantWhileCurrent()
    {
        database.RequireAvailable();
        var (tenant, owner) = await InsertTenantAsync("membership-unique");
        var subject = Subject("member-1");

        // A removed membership is history: the same subject may hold a new current membership.
        var removed = Membership.CreateMember(tenant.Id, subject, MerchantRole.MerchantStaff, Now);
        removed.Remove(activeOwnerCount: 1, Now);
        await SaveAsync(removed);
        await SaveAsync(Membership.CreateMember(tenant.Id, subject, MerchantRole.MerchantStaff, Now));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Membership.CreateMember(tenant.Id, subject, MerchantRole.MerchantAdmin, Now)));
        Assert.Equal(2601, Assert.IsType<SqlException>(duplicate.InnerException).Number);

        var duplicateOwner = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Membership.CreateOwner(tenant.Id, Subject(owner.SubjectId), Now)));
        Assert.Equal(2601, Assert.IsType<SqlException>(duplicateOwner.InnerException).Number);
    }

    [Theory]
    [InlineData("UPDATE Tenants SET Status = N'Deleted' WHERE Slug = 'checked'")]
    [InlineData("UPDATE Memberships SET Role = N'SuperAdmin' WHERE TenantId = (SELECT Id FROM Tenants WHERE Slug = 'checked')")]
    [InlineData("UPDATE Memberships SET Status = N'Pending' WHERE TenantId = (SELECT Id FROM Tenants WHERE Slug = 'checked')")]
    [InlineData("INSERT INTO Memberships (Id, TenantId, SubjectId, Role, Status, CreatedAt, StatusChangedAt) VALUES (NEWID(), NEWID(), N'orphan', N'MerchantStaff', N'Active', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET())")]
    public async Task Database_RejectsValuesOutsideTheModel(string statement)
    {
        database.RequireAvailable();
        if (await database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'checked'") == 0)
        {
            await InsertTenantAsync("checked");
        }

        var failure = await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync(statement));

        // 547: CHECK or FOREIGN KEY constraint violation.
        Assert.Equal(547, failure.Number);
    }

    [Fact]
    public async Task ConcurrentUpdates_OfOneMembership_FailOptimistically()
    {
        database.RequireAvailable();
        var (tenant, _) = await InsertTenantAsync("optimistic");
        var member = Membership.CreateMember(tenant.Id, Subject("contended"), MerchantRole.MerchantStaff, Now);
        await SaveAsync(member);

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var firstCopy = await first.Memberships.SingleAsync(m => m.Id == member.Id, TestContext.Current.CancellationToken);
        var secondCopy = await second.Memberships.SingleAsync(m => m.Id == member.Id, TestContext.Current.CancellationToken);

        Assert.Equal(MembershipChangeOutcome.Changed, firstCopy.Suspend(activeOwnerCount: 1, Now));
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MembershipChangeOutcome.Changed, secondCopy.ChangeRole(MerchantRole.MerchantAdmin, activeOwnerCount: 1, Now));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(TestContext.Current.CancellationToken));

        await using var verify = database.CreateContext();
        var stored = await verify.Memberships.SingleAsync(m => m.Id == member.Id, TestContext.Current.CancellationToken);
        Assert.Equal(MembershipStatus.Suspended, stored.Status);
        Assert.Equal(MerchantRole.MerchantStaff, stored.Role);
    }

    private async Task<(Tenant Tenant, Membership Owner)> InsertTenantAsync(string slug)
    {
        var (tenant, owner) = NewTenant(slug);
        await using var context = database.CreateContext();
        Assert.Equal(TenantInsertOutcome.Inserted, await new TenantProvisioningStore(context).AddAsync(tenant, owner, AuditOf(owner), Metadata, TestContext.Current.CancellationToken));
        return (tenant, owner);
    }

    private async Task SaveAsync(Membership membership)
    {
        await using var context = database.CreateContext();
        context.Memberships.Add(membership);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static MembershipAuditRecord AuditOf(Membership owner) =>
        MembershipAuditRecord.OwnerProvisioned(owner, AuditActor.Service("svc-test-admin"), correlationId: null, Now);

    private static (Tenant Tenant, Membership Owner) NewTenant(string slug, string ownerSubject = "owner", string? key = null, string client = "svc-test-admin")
    {
        Assert.True(TenantSlug.TryCreate(slug, out var tenantSlug));
        Assert.True(MerchantDisplayName.TryCreate($"{slug} store", out var name));
        Assert.True(ActorReference.TryCreate("admin:test", out var actor));
        var tenant = Tenant.Provision(tenantSlug, name, new ProvisioningRecord(client, key ?? $"key-{slug}", new byte[32], actor), Now);
        return (tenant, Membership.CreateOwner(tenant.Id, Subject(ownerSubject), Now));
    }

    private static SubjectId Subject(string value) =>
        SubjectId.TryCreate(value, out var subject) ? subject : throw new ArgumentException("Invalid test subject.", nameof(value));
}
