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

        Assert.Equal(["Memberships", "MerchantProfiles", "Tenants"], tables);
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
            .Select(property => property.GetColumnName(StoreObjectIdentifier.Table(property.DeclaringType.GetTableName()!)) ?? property.Name);

        Assert.DoesNotContain(columns, column => forbidden.Any(word => column.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Model_EveryTableHasARowVersion()
    {
        var withoutRowVersion = _context.Model.GetEntityTypes()
            .Where(entity => entity.FindProperty(TenantMerchantDbContext.RowVersion) is not { IsConcurrencyToken: true })
            .Select(entity => entity.GetTableName());

        Assert.Empty(withoutRowVersion);
    }

    public void Dispose() => _context.Dispose();
}
