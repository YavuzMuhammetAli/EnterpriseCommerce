using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>
/// The Tenant &amp; Merchant model owns exactly its own tables, with no reference to another service's data
/// (ADR-003, ADR-011) and no credential storage (Identity owns credentials, ADR-015).
/// </summary>
public sealed class PersistenceModelTests : IDisposable
{
    private readonly TenantMerchantDbContext _context = new(
        new DbContextOptionsBuilder<TenantMerchantDbContext>().UseSqlServer("Server=unused;Database=unused").Options);

    [Fact]
    public void Model_MapsOnlyTheServicesOwnTables()
    {
        var tables = _context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).Distinct().Order(StringComparer.Ordinal);

        Assert.Equal(["MembershipAuditRecords", "Memberships", "MerchantInvitations", "MerchantProfiles", "Tenants"], tables);
    }

    [Fact]
    public void Model_EntityTypesBelongToTenantMerchant()
    {
        var foreign = _context.Model.GetEntityTypes()
            .Select(entity => entity.ClrType)
            .Where(type => type.Namespace is null || !type.Namespace.StartsWith("EnterpriseCommerce.TenantMerchant.", StringComparison.Ordinal));

        Assert.Empty(foreign);
    }

    [Fact]
    public void Model_ForeignKeysStayInsideTheDatabase()
    {
        var targets = _context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Select(foreignKey => foreignKey.PrincipalEntityType.GetTableName())
            .Distinct();

        Assert.All(targets, table => Assert.Equal("Tenants", table));
    }

    [Fact]
    public void Model_StoresNoCredentials()
    {
        string[] forbidden = ["password", "secret", "token", "credential", "securitystamp"];
        var columns = _context.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Select(property => $"{property.DeclaringType.GetTableName()}.{property.GetColumnName(StoreObjectIdentifier.Table(property.DeclaringType.GetTableName()!)) ?? property.Name}");

        // The one deliberate exception (ADR-021): the SHA-256 hash of an invitation's acceptance token. The raw token is
        // never stored; Model_InvitationTokenHash_CanHoldOnlyAHash proves the column cannot hold it.
        Assert.DoesNotContain(
            columns.Where(column => column != InvitationTokenHashColumn),
            column => forbidden.Any(word => column.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Model_InvitationTokenHash_CanHoldOnlyAHash()
    {
        var property = _context.Model.FindEntityType(typeof(EnterpriseCommerce.TenantMerchant.Domain.Invitations.MerchantInvitation))!
            .FindProperty(nameof(EnterpriseCommerce.TenantMerchant.Domain.Invitations.MerchantInvitation.TokenHash))!;

        Assert.Equal("binary(32)", property.GetColumnType());
        Assert.True(property.IsFixedLength());
        Assert.True(property.GetContainingIndexes().Single().IsUnique);
    }

    [Fact]
    public void Model_EveryMutableTableHasARowVersion_AndTheAuditTableIsAppendOnly()
    {
        var withoutRowVersion = _context.Model.GetEntityTypes()
            .Where(entity => entity.FindProperty(TenantMerchantDbContext.RowVersion) is not { IsConcurrencyToken: true })
            .Select(entity => entity.GetTableName());

        // The audit table is never updated, so it has no concurrency token: its trigger refuses updates and deletes.
        Assert.Equal(["MembershipAuditRecords"], withoutRowVersion);
        var audit = _context.Model.FindEntityType(typeof(EnterpriseCommerce.TenantMerchant.Domain.Audit.MembershipAuditRecord))!;
        Assert.Contains(audit.GetDeclaredTriggers(), trigger => trigger.ModelName == TenantMerchantDbContext.AuditAppendOnlyTrigger);
    }

    [Fact]
    public void Model_MembershipsDeclareTheLastOwnerTrigger()
    {
        var memberships = _context.Model.FindEntityType(typeof(EnterpriseCommerce.TenantMerchant.Domain.Memberships.Membership))!;
        Assert.Contains(memberships.GetDeclaredTriggers(), trigger => trigger.ModelName == TenantMerchantDbContext.RequireActiveOwnerTrigger);
    }

    private const string InvitationTokenHashColumn = "MerchantInvitations.TokenHash";

    public void Dispose() => _context.Dispose();
}
