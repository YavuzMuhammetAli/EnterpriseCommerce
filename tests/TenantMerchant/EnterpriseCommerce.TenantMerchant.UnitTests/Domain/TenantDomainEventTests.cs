using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Domain;

public sealed class TenantDomainEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 15, 30, TimeSpan.Zero);

    [Fact]
    public void Provision_RecordsExactlyOneTenantCreated_WithTheTenantsValues()
    {
        var tenant = Tenant.Provision(Slug("northwind"), Name("Northwind Goods"), new ProvisioningRecord("svc-admin", "key-1", new byte[32], Actor("admin:1")), Now);

        var created = Assert.IsType<TenantCreated>(Assert.Single(tenant.DomainEvents));
        Assert.Equal(
            new TenantCreated(tenant.Id, "northwind", "Northwind Goods", TenantStatus.Active, TenantOrigin.Provisioned, Now),
            created);
    }

    [Fact]
    public void CreateBySelfService_RecordsExactlyOneTenantCreated_WithTheSelfServiceOrigin()
    {
        var tenant = Tenant.CreateBySelfService(Slug("northwind"), Name("Northwind Goods"), new SelfServiceRecord("bff-merchant", "key-1", new byte[32], Subject("user-1")), Now);

        var created = Assert.IsType<TenantCreated>(Assert.Single(tenant.DomainEvents));
        Assert.Equal(
            new TenantCreated(tenant.Id, "northwind", "Northwind Goods", TenantStatus.Active, TenantOrigin.SelfService, Now),
            created);
    }

    [Fact]
    public void ClearDomainEvents_ForgetsTheRecordedEvents()
    {
        var tenant = Tenant.Provision(Slug("northwind"), Name("Northwind Goods"), new ProvisioningRecord("svc-admin", "key-1", new byte[32], Actor("admin:1")), Now);

        tenant.ClearDomainEvents();

        Assert.Empty(tenant.DomainEvents);
    }

    private static TenantSlug Slug(string value) => TenantSlug.TryCreate(value, out var slug) ? slug : throw new ArgumentException(value);

    private static MerchantDisplayName Name(string value) => MerchantDisplayName.TryCreate(value, out var name) ? name! : throw new ArgumentException(value);

    private static ActorReference Actor(string value) => ActorReference.TryCreate(value, out var actor) ? actor : throw new ArgumentException(value);

    private static SubjectId Subject(string value) => SubjectId.TryCreate(value, out var subject) ? subject : throw new ArgumentException(value);
}
