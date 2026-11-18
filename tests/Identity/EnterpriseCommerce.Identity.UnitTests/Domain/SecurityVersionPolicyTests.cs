using EnterpriseCommerce.Identity.Domain.Security;

namespace EnterpriseCommerce.Identity.UnitTests.Domain;

public sealed class SecurityVersionPolicyTests
{
    [Fact]
    public void Evaluate_SameVersion_IsCurrent() =>
        Assert.Equal(SecurityVersionVerdict.Current, SecurityVersionPolicy.Evaluate(currentVersion: 3, sessionVersion: 3));

    [Fact]
    public void Evaluate_VersionRaisedAfterSessionStarted_IsStale() =>
        Assert.Equal(SecurityVersionVerdict.Stale, SecurityVersionPolicy.Evaluate(currentVersion: 4, sessionVersion: 3));

    [Fact]
    public void Evaluate_SessionVersionAboveCurrent_FailsClosedAsStale() =>
        Assert.Equal(SecurityVersionVerdict.Stale, SecurityVersionPolicy.Evaluate(currentVersion: 3, sessionVersion: 4));

    [Fact]
    public void Evaluate_MissingRecord_FailsClosed() =>
        Assert.Equal(SecurityVersionVerdict.MissingRecord, SecurityVersionPolicy.Evaluate(currentVersion: null, sessionVersion: 1));

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Evaluate_MissingOrInvalidSessionVersion_FailsClosed(long? sessionVersion) =>
        Assert.Equal(SecurityVersionVerdict.MissingSessionVersion, SecurityVersionPolicy.Evaluate(currentVersion: 1, sessionVersion));
}
