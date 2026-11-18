using System.Net;
using EnterpriseCommerce.Identity.Api.SecurityEmails;
using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;

namespace EnterpriseCommerce.Identity.UnitTests.Application;

public sealed class SecurityEmailApplicationTests
{
    [Theory]
    [InlineData("  Someone@Example.Test ", "Someone@Example.Test", "SOMEONE@EXAMPLE.TEST")]
    [InlineData("a.b+tag@sub.example.test", "a.b+tag@sub.example.test", "A.B+TAG@SUB.EXAMPLE.TEST")]
    public void AccountEmail_TrimsAndNormalizesLikeIdentity(string input, string email, string normalized)
    {
        Assert.True(AccountEmail.TryNormalize(input, out var actualEmail, out var actualNormalized));
        Assert.Equal(email, actualEmail);
        Assert.Equal(normalized, actualNormalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("Display Name <someone@example.test>")]
    [InlineData("someone@localhost")]
    [InlineData("some\u0001one@example.test")]
    public void AccountEmail_RejectsAnythingButAPlainAddress(string? input) =>
        Assert.False(AccountEmail.TryNormalize(input, out _, out _));

    [Fact]
    public void AccountEmail_RejectsOverlongAddresses() =>
        Assert.False(AccountEmail.TryNormalize(new string('a', 250) + "@example.test", out _, out _));

    [Fact]
    public void SecurityEmailMessage_NeverRendersTheLinkOrRecipient()
    {
        var message = new SecurityEmailMessage(
            Guid.NewGuid(),
            SecurityTokenPurpose.EmailConfirmation,
            "someone@example.test",
            new Uri("https://id.example.test/account/confirm-email?token=secret-token-value"));

        var rendered = message.ToString();

        Assert.DoesNotContain("secret-token-value", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("someone@example.test", rendered, StringComparison.Ordinal);
        Assert.Contains(message.DeliveryId.ToString(), rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted, SecurityEmailDispatchOutcome.Delivered)]
    [InlineData(HttpStatusCode.Conflict, SecurityEmailDispatchOutcome.Delivered)] // same Idempotency-Key already accepted
    [InlineData(HttpStatusCode.Unauthorized, SecurityEmailDispatchOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.TooManyRequests, SecurityEmailDispatchOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SecurityEmailDispatchOutcome.TransientFailure)]
    [InlineData(HttpStatusCode.BadRequest, SecurityEmailDispatchOutcome.PermanentFailure)]
    [InlineData(HttpStatusCode.Forbidden, SecurityEmailDispatchOutcome.PermanentFailure)]
    public void NotificationDispatcher_ClassifiesResponses(HttpStatusCode status, SecurityEmailDispatchOutcome expected) =>
        Assert.Equal(expected, NotificationSecurityEmailDispatcher.Classify(status));

    [Fact]
    public void NotificationOptions_RequireHttpsAndAServiceClient()
    {
        var configured = new NotificationDeliveryOptions
        {
            BaseAddress = new Uri("https://notification.internal/"),
            TokenEndpoint = new Uri("https://identity.internal/connect/token"),
            Issuer = new Uri("https://id.example.test/"),
            PrivateKeyPath = "/run/secrets/identity-notification.pem",
        };

        Assert.True(configured.IsConfigured);
        Assert.False(new NotificationDeliveryOptions().IsConfigured);
        configured.BaseAddress = new Uri("http://notification.internal/");
        Assert.False(configured.IsConfigured);
        configured.BaseAddress = new Uri("https://notification.internal/");
        configured.ClientId = "bff-storefront";
        Assert.False(configured.IsConfigured);
    }

    [Fact]
    public void MailSinkGuard_OutsideDevelopment_FailsOnAnySinkSettingOrAssembly()
    {
        Assert.NotNull(SecurityEmailSetup.Evaluate(false, sectionPresent: true, new DevelopmentMailSinkSettings(), assemblyPresent: false).Error);
        Assert.NotNull(SecurityEmailSetup.Evaluate(false, sectionPresent: false, new DevelopmentMailSinkSettings(), assemblyPresent: true).Error);
        Assert.Equal(new DevelopmentMailSinkDecision(false, null), SecurityEmailSetup.Evaluate(false, false, new DevelopmentMailSinkSettings(), false));
    }

    [Theory]
    [InlineData(false, "127.0.0.1", false, null)] // no opt-in: never registered
    [InlineData(true, "127.0.0.1", true, null)]
    [InlineData(true, "::1", true, null)]
    [InlineData(true, "localhost", true, null)]
    [InlineData(true, "mailpit", true, null)]
    [InlineData(true, "smtp.example.com", false, "Host")]
    [InlineData(true, "10.0.0.8", false, "Host")]
    public void MailSinkGuard_InDevelopment_RequiresOptInAndALocalHost(bool enabled, string host, bool register, string? errorFragment)
    {
        var decision = SecurityEmailSetup.Evaluate(true, true, new DevelopmentMailSinkSettings { Enabled = enabled, Host = host }, assemblyPresent: true);

        Assert.Equal(register, decision.Register);
        if (errorFragment is null)
        {
            Assert.Null(decision.Error);
        }
        else
        {
            Assert.Contains(errorFragment, decision.Error, StringComparison.Ordinal);
        }
    }
}
