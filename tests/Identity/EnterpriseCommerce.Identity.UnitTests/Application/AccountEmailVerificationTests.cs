using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Domain.Accounts;

namespace EnterpriseCommerce.Identity.UnitTests.Application;

/// <summary>The decision order of the email verification check (ADR-021).</summary>
public sealed class AccountEmailVerificationTests
{
    private const string Address = "ALICE@EXAMPLE.TEST";

    [Fact]
    public void UnknownAccount_IsNotFound() =>
        Assert.Equal(AccountEmailVerificationResult.NotFound, VerifyAccountEmailHandler.Evaluate(null, Address));

    [Fact]
    public void DisabledAccount_IsInactive_EvenWithAConfirmedMatchingAddress() =>
        Assert.Equal(
            AccountEmailVerificationResult.Inactive,
            VerifyAccountEmailHandler.Evaluate(new AccountEmailState(AccountStatus.Disabled, EmailConfirmed: true, Address), Address));

    [Fact]
    public void UnconfirmedAddress_IsUnverified_EvenWhenItMatches() =>
        Assert.Equal(
            AccountEmailVerificationResult.Unverified,
            VerifyAccountEmailHandler.Evaluate(new AccountEmailState(AccountStatus.Active, EmailConfirmed: false, Address), Address));

    [Theory]
    [InlineData("BOB@EXAMPLE.TEST")]
    [InlineData("alice@example.test")]
    [InlineData("ALICE@EXAMPLE.TEST ")]
    public void ADifferentNormalizedAddress_IsAMismatch(string candidate) =>
        Assert.Equal(
            AccountEmailVerificationResult.Mismatch,
            VerifyAccountEmailHandler.Evaluate(new AccountEmailState(AccountStatus.Active, EmailConfirmed: true, Address), candidate));

    [Fact]
    public void AnAccountWithoutAnAddress_IsAMismatch() =>
        Assert.Equal(
            AccountEmailVerificationResult.Mismatch,
            VerifyAccountEmailHandler.Evaluate(new AccountEmailState(AccountStatus.Active, EmailConfirmed: true, NormalizedEmail: null), Address));

    [Fact]
    public void ActiveConfirmedEqualAddress_IsAMatch() =>
        Assert.Equal(
            AccountEmailVerificationResult.Match,
            VerifyAccountEmailHandler.Evaluate(new AccountEmailState(AccountStatus.Active, EmailConfirmed: true, Address), Address));
}
