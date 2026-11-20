using EnterpriseCommerce.Identity.Domain.Accounts;

namespace EnterpriseCommerce.Identity.UnitTests.Domain;

public sealed class AccountLifecycleTests
{
    [Theory]
    [InlineData(AccountStatus.Active, AccountStatus.Disabled, true)]
    [InlineData(AccountStatus.Disabled, AccountStatus.Active, true)]
    [InlineData(AccountStatus.Active, AccountStatus.Active, false)]
    [InlineData(AccountStatus.Disabled, AccountStatus.Disabled, false)]
    public void OnlyTheTwoAdministrativeTransitionsExist(AccountStatus from, AccountStatus to, bool allowed) =>
        Assert.Equal(allowed, AccountLifecycle.CanTransition(from, to));

    [Theory]
    [InlineData("admin:alice")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("svc.admin_op-7")]
    public void OpaqueActorReferences_AreAccepted(string value) =>
        Assert.True(AccountLifecycle.IsValidActorReference(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alice@example.test")]
    [InlineData("admin alice")]
    [InlineData("admin\nforged log line")]
    [InlineData("<script>")]
    public void ActorReferencesThatCouldCarryPersonalDataOrForgeLogs_AreRejected(string? value) =>
        Assert.False(AccountLifecycle.IsValidActorReference(value));

    [Fact]
    public void ActorReferences_AreBoundedInLength()
    {
        Assert.True(AccountLifecycle.IsValidActorReference(new string('a', AccountLifecycle.ActorReferenceMaxLength)));
        Assert.False(AccountLifecycle.IsValidActorReference(new string('a', AccountLifecycle.ActorReferenceMaxLength + 1)));
    }

    [Fact]
    public void OnlyDefinedReasonCodes_AreValid()
    {
        Assert.True(AccountLifecycle.IsDefined(AccountStatusReason.SecurityConcern));
        Assert.False(AccountLifecycle.IsDefined((AccountStatusReason)42));
    }
}
