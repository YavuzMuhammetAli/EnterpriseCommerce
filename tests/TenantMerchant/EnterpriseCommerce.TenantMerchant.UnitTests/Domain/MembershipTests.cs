using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Domain;

public sealed class MembershipTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateOwner_CreatesAnActiveOwnerMembership()
    {
        var owner = Membership.CreateOwner(TenantId, Subject("owner"), Now);

        Assert.Equal(TenantId, owner.TenantId);
        Assert.Equal("owner", owner.SubjectId);
        Assert.Equal(MerchantRole.MerchantOwner, owner.Role);
        Assert.Equal(MembershipStatus.Active, owner.Status);
        Assert.Equal(Now, owner.CreatedAt);
        Assert.Equal(Now, owner.StatusChangedAt);
        Assert.NotEqual(Guid.Empty, owner.Id);
    }

    [Fact]
    public void CreateMember_NeverGrantsOwner()
    {
        Assert.Throws<ArgumentException>(() => Membership.CreateMember(TenantId, Subject("someone"), MerchantRole.MerchantOwner, Now));
    }

    [Fact]
    public void Create_RequiresATenant()
    {
        Assert.Throws<ArgumentException>(() => Membership.CreateOwner(Guid.Empty, Subject("owner"), Now));
    }

    [Theory]
    [InlineData(MerchantRole.MerchantAdmin)]
    [InlineData(MerchantRole.MerchantStaff)]
    public void ChangeRole_RefusesPromotionToOwner(MerchantRole current)
    {
        var member = Membership.CreateMember(TenantId, Subject("member"), current, Now);

        var outcome = member.ChangeRole(MerchantRole.MerchantOwner, activeOwnerCount: 1, Now);

        Assert.Equal(MembershipChangeOutcome.OwnerRoleReserved, outcome);
        Assert.Equal(current, member.Role);
    }

    [Fact]
    public void ChangeRole_ChangesAnActiveNonOwnerRole()
    {
        var member = Membership.CreateMember(TenantId, Subject("member"), MerchantRole.MerchantStaff, Now);

        Assert.Equal(MembershipChangeOutcome.Changed, member.ChangeRole(MerchantRole.MerchantAdmin, activeOwnerCount: 1, Now));
        Assert.Equal(MerchantRole.MerchantAdmin, member.Role);
        Assert.Equal(MembershipChangeOutcome.Unchanged, member.ChangeRole(MerchantRole.MerchantAdmin, activeOwnerCount: 1, Now));
    }

    [Fact]
    public void ChangeRole_RefusesAnInactiveMembership()
    {
        var member = Membership.CreateMember(TenantId, Subject("member"), MerchantRole.MerchantStaff, Now);
        member.Suspend(activeOwnerCount: 1, Now);

        Assert.Equal(MembershipChangeOutcome.InvalidTransition, member.ChangeRole(MerchantRole.MerchantAdmin, activeOwnerCount: 1, Now));
        Assert.Equal(MerchantRole.MerchantStaff, member.Role);
    }

    [Fact]
    public void ChangeRole_NeverDemotesTheLastActiveOwner()
    {
        var owner = Membership.CreateOwner(TenantId, Subject("owner"), Now);

        Assert.Equal(MembershipChangeOutcome.LastActiveOwner, owner.ChangeRole(MerchantRole.MerchantAdmin, activeOwnerCount: 1, Now));
        Assert.Equal(MerchantRole.MerchantOwner, owner.Role);
        Assert.Equal(MembershipChangeOutcome.Changed, owner.ChangeRole(MerchantRole.MerchantAdmin, activeOwnerCount: 2, Now));
    }

    [Fact]
    public void Status_SuspendReactivateAndRemove()
    {
        var member = Membership.CreateMember(TenantId, Subject("member"), MerchantRole.MerchantStaff, Now);
        var later = Now.AddMinutes(5);

        Assert.Equal(MembershipChangeOutcome.Changed, member.Suspend(activeOwnerCount: 1, later));
        Assert.Equal(MembershipStatus.Suspended, member.Status);
        Assert.Equal(later, member.StatusChangedAt);
        Assert.Equal(MembershipChangeOutcome.Unchanged, member.Suspend(activeOwnerCount: 1, later));

        Assert.Equal(MembershipChangeOutcome.Changed, member.Reactivate(later));
        Assert.Equal(MembershipStatus.Active, member.Status);

        Assert.Equal(MembershipChangeOutcome.Changed, member.Remove(activeOwnerCount: 1, later));
        Assert.Equal(MembershipStatus.Removed, member.Status);
    }

    [Fact]
    public void Removed_IsTerminal()
    {
        var member = Membership.CreateMember(TenantId, Subject("member"), MerchantRole.MerchantStaff, Now);
        member.Remove(activeOwnerCount: 1, Now);

        Assert.Equal(MembershipChangeOutcome.InvalidTransition, member.Reactivate(Now));
        Assert.Equal(MembershipChangeOutcome.InvalidTransition, member.Suspend(activeOwnerCount: 1, Now));
        Assert.Equal(MembershipChangeOutcome.Unchanged, member.Remove(activeOwnerCount: 1, Now));
        Assert.Equal(MembershipStatus.Removed, member.Status);
    }

    [Fact]
    public void Reactivate_OfAnActiveMembership_IsUnchanged()
    {
        var member = Membership.CreateMember(TenantId, Subject("member"), MerchantRole.MerchantStaff, Now);

        Assert.Equal(MembershipChangeOutcome.Unchanged, member.Reactivate(Now));
    }

    [Fact]
    public void TheLastActiveOwner_CannotBeSuspendedOrRemoved()
    {
        var owner = Membership.CreateOwner(TenantId, Subject("owner"), Now);

        Assert.Equal(MembershipChangeOutcome.LastActiveOwner, owner.Suspend(activeOwnerCount: 1, Now));
        Assert.Equal(MembershipChangeOutcome.LastActiveOwner, owner.Remove(activeOwnerCount: 1, Now));
        Assert.Equal(MembershipStatus.Active, owner.Status);

        Assert.Equal(MembershipChangeOutcome.Changed, owner.Suspend(activeOwnerCount: 2, Now));
    }

    [Theory]
    [InlineData(MembershipStatus.Active, MembershipStatus.Suspended, true)]
    [InlineData(MembershipStatus.Suspended, MembershipStatus.Active, true)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Removed, true)]
    [InlineData(MembershipStatus.Suspended, MembershipStatus.Removed, true)]
    [InlineData(MembershipStatus.Removed, MembershipStatus.Active, false)]
    [InlineData(MembershipStatus.Removed, MembershipStatus.Suspended, false)]
    [InlineData(MembershipStatus.Active, MembershipStatus.Active, false)]
    public void TransitionMatrix(MembershipStatus from, MembershipStatus to, bool allowed)
    {
        Assert.Equal(allowed, MembershipRules.CanTransition(from, to));
    }

    [Theory]
    [InlineData(MerchantRole.MerchantOwner, MerchantPermission.ViewTenant, true)]
    [InlineData(MerchantRole.MerchantAdmin, MerchantPermission.ViewTenant, true)]
    [InlineData(MerchantRole.MerchantStaff, MerchantPermission.ViewTenant, true)]
    [InlineData(MerchantRole.MerchantOwner, MerchantPermission.ViewMemberships, true)]
    [InlineData(MerchantRole.MerchantAdmin, MerchantPermission.ViewMemberships, true)]
    [InlineData(MerchantRole.MerchantStaff, MerchantPermission.ViewMemberships, false)]
    public void PermissionMatrix(MerchantRole role, MerchantPermission permission, bool allowed)
    {
        Assert.Equal(allowed, MerchantRolePermissions.Allows(role, permission));
    }

    private static SubjectId Subject(string value) =>
        SubjectId.TryCreate(value, out var subject) ? subject : throw new ArgumentException("Invalid test subject.", nameof(value));
}
