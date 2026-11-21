using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Application;

public sealed class TenantAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryTenantStore _store = new();
    private readonly TenantAccessAuthorizer _authorizer;
    private readonly Tenant _acme;
    private readonly Tenant _globex;

    public TenantAccessTests()
    {
        _authorizer = new TenantAccessAuthorizer(_store, NullLogger<TenantAccessAuthorizer>.Instance);
        _acme = AddTenant("acme", "owner-a");
        _globex = AddTenant("globex", "owner-g");
    }

    [Theory]
    [InlineData(MerchantRole.MerchantAdmin, MerchantPermission.ViewMemberships)]
    [InlineData(MerchantRole.MerchantStaff, MerchantPermission.ViewTenant)]
    public async Task ActiveMembersWithThePermission_AreAllowed(MerchantRole role, MerchantPermission permission)
    {
        AddMember(_acme.Id, "member", role);

        var decision = await _authorizer.AuthorizeAsync(_acme.Id, "member", permission, TestContext.Current.CancellationToken);

        Assert.Equal(TenantAccess.Allowed, decision.Access);
        Assert.Equal(role, decision.Membership!.Role);
    }

    [Fact]
    public async Task TheOwner_IsAllowedEverything()
    {
        foreach (var permission in Enum.GetValues<MerchantPermission>())
        {
            var decision = await _authorizer.AuthorizeAsync(_acme.Id, "owner-a", permission, TestContext.Current.CancellationToken);
            Assert.Equal(TenantAccess.Allowed, decision.Access);
        }
    }

    [Fact]
    public async Task StaffListingMemberships_IsForbidden()
    {
        AddMember(_acme.Id, "staff", MerchantRole.MerchantStaff);

        var decision = await _authorizer.AuthorizeAsync(_acme.Id, "staff", MerchantPermission.ViewMemberships, TestContext.Current.CancellationToken);

        Assert.Equal(TenantAccess.Forbidden, decision.Access);
    }

    [Fact]
    public async Task AnOwnerOfAnotherTenant_SeesNothing()
    {
        var decision = await _authorizer.AuthorizeAsync(_globex.Id, "owner-a", MerchantPermission.ViewTenant, TestContext.Current.CancellationToken);

        Assert.Equal(TenantAccess.NotVisible, decision.Access);
        Assert.Null(decision.Membership);
    }

    [Fact]
    public async Task AnUnknownTenant_IsNotVisible()
    {
        var decision = await _authorizer.AuthorizeAsync(Guid.CreateVersion7(), "owner-a", MerchantPermission.ViewTenant, TestContext.Current.CancellationToken);

        Assert.Equal(TenantAccess.NotVisible, decision.Access);
    }

    [Fact]
    public async Task SuspendedAndRemovedMembers_SeeNothing()
    {
        AddMember(_acme.Id, "suspended", MerchantRole.MerchantAdmin).Suspend(activeOwnerCount: 1, Now);
        AddMember(_acme.Id, "removed", MerchantRole.MerchantAdmin).Remove(activeOwnerCount: 1, Now);

        foreach (var subject in new[] { "suspended", "removed" })
        {
            var decision = await _authorizer.AuthorizeAsync(_acme.Id, subject, MerchantPermission.ViewTenant, TestContext.Current.CancellationToken);
            Assert.Equal(TenantAccess.NotVisible, decision.Access);
        }
    }

    [Fact]
    public async Task GetTenantForMember_ReturnsTheTenantAndTheCallersMembership()
    {
        var handler = new GetTenantForMemberHandler(_authorizer, _store);

        var result = await handler.HandleAsync(_acme.Id, "owner-a", TestContext.Current.CancellationToken);

        Assert.Equal(TenantAccess.Allowed, result.Access);
        Assert.Equal("acme", result.Tenant!.Slug);
        Assert.Equal("owner-a", result.Membership!.SubjectId);
    }

    [Fact]
    public async Task ListMemberships_PagesTheCurrentMembershipsOfOneTenantOnly()
    {
        for (var index = 0; index < 3; index++)
        {
            AddMember(_acme.Id, $"staff-{index}", MerchantRole.MerchantStaff);
        }

        AddMember(_acme.Id, "gone", MerchantRole.MerchantStaff).Remove(activeOwnerCount: 1, Now);
        var handler = new ListMembershipsHandler(_authorizer, _store);
        Assert.True(PageRequest.TryCreate(2, 2, out var page, out _));

        var result = await handler.HandleAsync(_acme.Id, "owner-a", page, TestContext.Current.CancellationToken);

        Assert.Equal(TenantAccess.Allowed, result.Access);
        Assert.Equal(4, result.Page!.TotalCount);
        Assert.Equal(2, result.Page.Page);
        Assert.Equal(2, result.Page.PageSize);
        Assert.Equal(["staff-1", "staff-2"], result.Page.Items.Select(m => m.SubjectId));
        Assert.All(result.Page.Items, m => Assert.Equal(_acme.Id, m.TenantId));
    }

    private Tenant AddTenant(string slug, string owner)
    {
        TenantSlug.TryCreate(slug, out var tenantSlug);
        MerchantDisplayName.TryCreate(slug, out var name);
        ActorReference.TryCreate("admin", out var actor);
        SubjectId.TryCreate(owner, out var subject);
        var tenant = Tenant.Provision(tenantSlug!, name!, new ProvisioningRecord("svc-admin", slug, new byte[32], actor!), Now);
        _store.Tenants.Add(tenant);
        _store.Memberships.Add(Membership.CreateOwner(tenant.Id, subject!, Now));
        return tenant;
    }

    private Membership AddMember(Guid tenantId, string subject, MerchantRole role)
    {
        SubjectId.TryCreate(subject, out var subjectId);
        var membership = Membership.CreateMember(tenantId, subjectId!, role, Now.AddSeconds(_store.Memberships.Count));
        _store.Memberships.Add(membership);
        return membership;
    }
}
