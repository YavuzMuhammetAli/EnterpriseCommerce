using EnterpriseCommerce.Identity.Domain.Security;

namespace EnterpriseCommerce.Identity.UnitTests.Domain;

public sealed class AccountSecurityTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly string ValidHash = new('a', AccountSecurityToken.TokenHashLength);

    [Fact]
    public void Issue_SetsExpiryFromLifetime_AndIsConsumableUntilExpiry()
    {
        var token = AccountSecurityToken.Issue("user-1", SecurityTokenPurpose.PasswordReset, ValidHash, Now, TimeSpan.FromMinutes(30));

        Assert.NotEqual(Guid.Empty, token.Id);
        Assert.Equal(Now.AddMinutes(30), token.ExpiresAt);
        Assert.True(token.IsConsumableAt(Now));
        Assert.True(token.IsConsumableAt(Now.AddMinutes(30).AddTicks(-1)));
        Assert.False(token.IsConsumableAt(Now.AddMinutes(30)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")] // uppercase
    [InlineData("abc")] // too short
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // not hex
    public void Issue_RejectsAnythingButALowercaseSha256Hash(string hash) =>
        Assert.ThrowsAny<ArgumentException>(() =>
            AccountSecurityToken.Issue("user-1", SecurityTokenPurpose.EmailConfirmation, hash, Now, TimeSpan.FromHours(1)));

    [Fact]
    public void Issue_RejectsNonPositiveLifetime() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AccountSecurityToken.Issue("user-1", SecurityTokenPurpose.EmailConfirmation, ValidHash, Now, TimeSpan.Zero));

    [Fact]
    public void Issue_RejectsUndefinedPurpose() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AccountSecurityToken.Issue("user-1", (SecurityTokenPurpose)99, ValidHash, Now, TimeSpan.FromHours(1)));
}
