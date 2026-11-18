using EnterpriseCommerce.Identity.Domain.Security;

namespace EnterpriseCommerce.Identity.UnitTests.Domain;

public sealed class RefreshReusePolicyTests
{
    private const string Admin = "bff-admin";

    [Theory]
    [InlineData("bff-storefront", "bff-merchant")]
    [InlineData("bff-admin", "bff-storefront")]
    [InlineData(null, "bff-storefront")]
    [InlineData("bff-storefront", null)]
    [InlineData("bff-storefront", "BFF-STOREFRONT")]
    public void Decide_PresenterOtherThanTheTokenClient_RejectsWithoutRevocation(string? tokenClient, string? presenter) =>
        Assert.Equal(RefreshReuseAction.RejectPresenterMismatch, RefreshReusePolicy.Decide(tokenClient, presenter, Admin));

    [Fact]
    public void Decide_ReuseByTheAdminConsole_RevokesAllSessions() =>
        Assert.Equal(RefreshReuseAction.RevokeAllSessions, RefreshReusePolicy.Decide(Admin, Admin, Admin));

    [Theory]
    [InlineData("bff-storefront")]
    [InlineData("bff-merchant")]
    public void Decide_ReuseByAnotherClient_RevokesOnlyThatSession(string client) =>
        Assert.Equal(RefreshReuseAction.RevokeSession, RefreshReusePolicy.Decide(client, client, Admin));
}
