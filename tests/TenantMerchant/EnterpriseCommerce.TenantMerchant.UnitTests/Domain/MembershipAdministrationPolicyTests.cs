using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Domain;

/// <summary>
/// The complete role matrix of ADR-021 (PHASE_3_S2 section 3), cell by cell: every actor role against every target role
/// and action, plus invitations and the permissions. A changed cell fails here before it reaches an endpoint.
/// </summary>
public sealed class MembershipAdministrationPolicyTests
{
    private const MerchantRole Owner = MerchantRole.MerchantOwner;
    private const MerchantRole Admin = MerchantRole.MerchantAdmin;
    private const MerchantRole Staff = MerchantRole.MerchantStaff;

    public static TheoryData<MerchantRole, MerchantRole, MembershipAdministrationAction, bool> StatusMatrix()
    {
        var data = new TheoryData<MerchantRole, MerchantRole, MembershipAdministrationAction, bool>();
        foreach (var action in new[] { MembershipAdministrationAction.Suspend, MembershipAdministrationAction.Reactivate, MembershipAdministrationAction.Remove })
        {
            data.Add(Owner, Owner, action, true);
            data.Add(Owner, Admin, action, true);
            data.Add(Owner, Staff, action, true);
            data.Add(Admin, Owner, action, false);
            data.Add(Admin, Admin, action, false);
            data.Add(Admin, Staff, action, true);
            data.Add(Staff, Owner, action, false);
            data.Add(Staff, Admin, action, false);
            data.Add(Staff, Staff, action, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StatusMatrix))]
    public void StatusChanges_FollowTheMatrix(MerchantRole actor, MerchantRole target, MembershipAdministrationAction action, bool allowed) =>
        Assert.Equal(allowed, MembershipAdministrationPolicy.Allows(actor, target, action));

    [Theory]
    [InlineData(Owner, Owner, Admin, true)]
    [InlineData(Owner, Admin, Staff, true)]
    [InlineData(Owner, Staff, Admin, true)]
    [InlineData(Admin, Staff, Admin, false)]
    [InlineData(Admin, Staff, Staff, false)]
    [InlineData(Admin, Admin, Staff, false)]
    [InlineData(Staff, Staff, Admin, false)]
    public void RoleChanges_AreAnOwnersOnly(MerchantRole actor, MerchantRole target, MerchantRole newRole, bool allowed) =>
        Assert.Equal(allowed, MembershipAdministrationPolicy.Allows(actor, target, MembershipAdministrationAction.ChangeRole, newRole));

    [Fact]
    public void ARoleChange_WithoutARole_IsNeverAllowed() =>
        Assert.False(MembershipAdministrationPolicy.Allows(Owner, Staff, MembershipAdministrationAction.ChangeRole, newRole: null));

    [Fact]
    public void GrantingOwner_IsRefusedByTheMembership_WhoeverAsks()
    {
        // The policy lets an owner try; the membership itself refuses the reserved role.
        var staff = Membership.CreateMember(Guid.NewGuid(), Subject("staff"), Staff, DateTimeOffset.UtcNow);
        Assert.Equal(MembershipChangeOutcome.OwnerRoleReserved, staff.ChangeRole(Owner, activeOwnerCount: 3, DateTimeOffset.UtcNow));
        Assert.Equal(Staff, staff.Role);
    }

    [Theory]
    [InlineData(Owner, Admin, true)]
    [InlineData(Owner, Staff, true)]
    [InlineData(Owner, Owner, false)]
    [InlineData(Admin, Staff, true)]
    [InlineData(Admin, Admin, false)]
    [InlineData(Admin, Owner, false)]
    [InlineData(Staff, Staff, false)]
    [InlineData(Staff, Admin, false)]
    [InlineData(Staff, Owner, false)]
    public void Invitations_FollowTheMatrix(MerchantRole actor, MerchantRole invited, bool allowed)
    {
        Assert.Equal(allowed, MembershipAdministrationPolicy.CanInvite(actor, invited));
        Assert.Equal(allowed, MembershipAdministrationPolicy.CanCancelInvitation(actor, invited));
    }

    [Theory]
    [InlineData(MembershipAdministrationAction.Suspend)]
    [InlineData(MembershipAdministrationAction.Reactivate)]
    [InlineData(MembershipAdministrationAction.Remove)]
    [InlineData(MembershipAdministrationAction.ChangeRole)]
    public void NoMember_ChangesTheirOwnMembership(MembershipAdministrationAction action)
    {
        var tenantId = Guid.NewGuid();
        var owner = Membership.CreateOwner(tenantId, Subject("owner"), DateTimeOffset.UtcNow);

        Assert.Equal(AdministrationDecision.SelfModification, MembershipAdministrationPolicy.Decide(owner, owner, action, Admin));
    }

    [Fact]
    public void Decide_AllowsOrForbidsByRole_AndRefusesCrossTenantPairs()
    {
        var tenantId = Guid.NewGuid();
        var admin = Membership.CreateMember(tenantId, Subject("admin"), Admin, DateTimeOffset.UtcNow);
        var staff = Membership.CreateMember(tenantId, Subject("staff"), Staff, DateTimeOffset.UtcNow);
        var owner = Membership.CreateOwner(tenantId, Subject("owner"), DateTimeOffset.UtcNow);
        var foreign = Membership.CreateMember(Guid.NewGuid(), Subject("foreign"), Staff, DateTimeOffset.UtcNow);

        Assert.Equal(AdministrationDecision.Allowed, MembershipAdministrationPolicy.Decide(admin, staff, MembershipAdministrationAction.Suspend));
        Assert.Equal(AdministrationDecision.Forbidden, MembershipAdministrationPolicy.Decide(admin, owner, MembershipAdministrationAction.Suspend));
        Assert.Throws<ArgumentException>(() => MembershipAdministrationPolicy.Decide(admin, foreign, MembershipAdministrationAction.Suspend));
    }

    [Theory]
    [InlineData(Owner, MerchantPermission.ViewTenant, true)]
    [InlineData(Owner, MerchantPermission.ViewMemberships, true)]
    [InlineData(Owner, MerchantPermission.ViewInvitations, true)]
    [InlineData(Owner, MerchantPermission.ManageInvitations, true)]
    [InlineData(Owner, MerchantPermission.ManageMemberships, true)]
    [InlineData(Owner, MerchantPermission.ChangeRoles, true)]
    [InlineData(Admin, MerchantPermission.ViewTenant, true)]
    [InlineData(Admin, MerchantPermission.ViewMemberships, true)]
    [InlineData(Admin, MerchantPermission.ViewInvitations, true)]
    [InlineData(Admin, MerchantPermission.ManageInvitations, true)]
    [InlineData(Admin, MerchantPermission.ManageMemberships, true)]
    [InlineData(Admin, MerchantPermission.ChangeRoles, false)]
    [InlineData(Staff, MerchantPermission.ViewTenant, true)]
    [InlineData(Staff, MerchantPermission.ViewMemberships, false)]
    [InlineData(Staff, MerchantPermission.ViewInvitations, false)]
    [InlineData(Staff, MerchantPermission.ManageInvitations, false)]
    [InlineData(Staff, MerchantPermission.ManageMemberships, false)]
    [InlineData(Staff, MerchantPermission.ChangeRoles, false)]
    public void Permissions_FollowTheMatrix(MerchantRole role, MerchantPermission permission, bool allowed) =>
        Assert.Equal(allowed, MerchantRolePermissions.Allows(role, permission));

    [Fact]
    public void TheLastActiveOwner_CannotBeSuspendedRemovedOrDemoted_EvenByAnotherOwner()
    {
        var owner = Membership.CreateOwner(Guid.NewGuid(), Subject("owner"), DateTimeOffset.UtcNow);

        Assert.Equal(MembershipChangeOutcome.LastActiveOwner, owner.Suspend(activeOwnerCount: 1, DateTimeOffset.UtcNow));
        Assert.Equal(MembershipChangeOutcome.LastActiveOwner, owner.Remove(activeOwnerCount: 1, DateTimeOffset.UtcNow));
        Assert.Equal(MembershipChangeOutcome.LastActiveOwner, owner.ChangeRole(Admin, activeOwnerCount: 1, DateTimeOffset.UtcNow));
        Assert.Equal(MembershipChangeOutcome.Changed, owner.Suspend(activeOwnerCount: 2, DateTimeOffset.UtcNow));
    }

    private static SubjectId Subject(string value)
    {
        Assert.True(SubjectId.TryCreate(value, out var subject));
        return subject;
    }
}
