using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Domain;

public sealed class SelfServiceTenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateBySelfService_RecordsTheOriginTheSubjectAndTheRequest()
    {
        var tenant = Tenant.CreateBySelfService(Slug("acme"), Name("Acme"), new SelfServiceRecord("bff-merchant", "key-1", new byte[32], Subject("user-1")), Now);

        Assert.Equal(TenantOrigin.SelfService, tenant.Origin);
        Assert.Equal("user-1", tenant.SelfServiceSubjectId);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(("bff-merchant", "key-1", "user-1"), (tenant.ProvisionedByClientId, tenant.ProvisioningKey, tenant.ProvisionedByActor));
        Assert.Equal(("Acme", Now, Now), (tenant.MerchantProfile.DisplayName, tenant.MerchantProfile.CreatedAt, tenant.MerchantProfile.UpdatedAt));
        Assert.Equal(7, tenant.Id.Version);
    }

    [Fact]
    public void Provision_IsAProvisionedTenantWithoutASelfServiceSubject()
    {
        Assert.True(ActorReference.TryCreate("admin:1", out var actor));

        var tenant = Tenant.Provision(Slug("acme"), Name("Acme"), new ProvisioningRecord("svc-admin", "key-1", new byte[32], actor), Now);

        Assert.Equal(TenantOrigin.Provisioned, tenant.Origin);
        Assert.Null(tenant.SelfServiceSubjectId);
    }

    [Fact]
    public void TheSelfServiceAuditRecord_NamesTheUserAsActorAndOwner()
    {
        var owner = Membership.CreateOwner(Guid.CreateVersion7(Now), Subject("user-1"), Now);

        var record = MembershipAuditRecord.SelfServiceOwnerCreated(owner, "corr-1", Now);

        Assert.Equal(MembershipAuditAction.SelfServiceOwnerMembershipCreated, record.Action);
        Assert.Equal((AuditActorKind.User, "user-1"), (record.ActorKind, record.ActorId));
        Assert.Equal((owner.TenantId, owner.Id, "user-1"), (record.TenantId, record.MembershipId!.Value, record.SubjectId));
        Assert.Equal(("MerchantOwner", "Active"), (record.NewRole, record.NewStatus));
        Assert.Null(record.PreviousRole);
        Assert.Null(record.PreviousStatus);
        Assert.Null(record.InvitationId);
        Assert.Equal("corr-1", record.CorrelationId);
    }

    [Fact]
    public void TheSelfServiceAuditRecord_RefusesAMembershipThatIsNotAnOwner()
    {
        var staff = Membership.CreateMember(Guid.CreateVersion7(Now), Subject("user-1"), MerchantRole.MerchantStaff, Now);

        Assert.Throws<ArgumentException>(() => MembershipAuditRecord.SelfServiceOwnerCreated(staff, null, Now));
    }

    private static TenantSlug Slug(string value)
    {
        Assert.True(TenantSlug.TryCreate(value, out var slug));
        return slug;
    }

    private static MerchantDisplayName Name(string value)
    {
        Assert.True(MerchantDisplayName.TryCreate(value, out var name));
        return name;
    }

    private static SubjectId Subject(string value)
    {
        Assert.True(SubjectId.TryCreate(value, out var subject));
        return subject;
    }
}
