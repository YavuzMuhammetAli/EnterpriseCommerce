using System.Net;
using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Registration, email confirmation and resend through the real Identity host and worker (ADR-017 section 9,
/// ADR-018 sections 1 to 3).
/// </summary>
public sealed class RegistrationTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    [Fact]
    public async Task Registration_CreatesAnUnconfirmedAccountWithItsSecurityVersion_AndSendsOnlyAHashedToken()
    {
        identity.RequireAvailable();
        var email = NewEmail();
        using var browser = identity.CreateBrowser();

        using var response = await identity.RegisterAsync(browser, email);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/check-email", response.Headers.Location?.OriginalString);
        Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [], c => c.StartsWith("__Host-ec-identity=", StringComparison.Ordinal));

        var message = Assert.Single(await identity.Dispatcher.WaitForAsync(email));
        var rawToken = CapturingSecurityEmailDispatcher.TokenOf(message);
        Assert.Equal(new Uri(IdentityServerFixture.Issuer, "account/confirm-email").AbsoluteUri, message.Link.GetLeftPart(UriPartial.Path));

        await using var scope = identity.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        var user = await context.Users.AsNoTracking().SingleAsync(u => u.NormalizedEmail == email.ToUpperInvariant(), TestContext.Current.CancellationToken);
        Assert.False(user.EmailConfirmed);
        Assert.Equal(AccountStatus.Active, user.Status);
        Assert.Equal(1, await context.SecurityVersions.Where(v => v.UserId == user.Id).Select(v => v.Version).SingleAsync(TestContext.Current.CancellationToken));

        var token = await context.AccountSecurityTokens.AsNoTracking().SingleAsync(t => t.UserId == user.Id, TestContext.Current.CancellationToken);
        Assert.Equal(SecurityTokenCodec.Hash(rawToken), token.TokenHash);
        Assert.NotEqual(rawToken, token.TokenHash);
        Assert.InRange(token.ExpiresAt - token.CreatedAt, TimeSpan.FromHours(24) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        var delivery = await WaitForFinalDeliveryAsync(context, user.Id);
        Assert.Equal(SecurityEmailDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(token.Id, delivery.SecurityTokenId);
    }

    [Fact]
    public async Task Registration_WithAnExistingEmail_LooksIdentical_AndCreatesNothing()
    {
        identity.RequireAvailable();
        var existing = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();

        using var duplicate = await identity.RegisterAsync(browser, existing.ToUpperInvariant());
        using var fresh = await identity.RegisterAsync(browser, NewEmail());

        Assert.Equal(fresh.StatusCode, duplicate.StatusCode);
        Assert.Equal(fresh.Headers.Location, duplicate.Headers.Location);

        await using var scope = identity.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        Assert.Equal(1, await context.Users.CountAsync(u => u.NormalizedEmail == existing.ToUpperInvariant(), TestContext.Current.CancellationToken));

        // The existing account is confirmed, so nothing is sent to it.
        var userId = await identity.GetUserIdAsync(existing);
        Assert.False(await context.SecurityEmailDeliveries.AnyAsync(d => d.UserId == userId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not-an-email", IdentityServerFixture.Password)]
    [InlineData("Display Name <someone@example.test>", IdentityServerFixture.Password)]
    [InlineData("valid@example.test", "short")]
    [InlineData("valid@example.test", "alllowercaseandlong")]
    public async Task Registration_WithInvalidInput_ShowsErrors_AndCreatesNoAccount(string email, string password)
    {
        identity.RequireAvailable();
        using var browser = identity.CreateBrowser();

        using var response = await identity.RegisterAsync(browser, email, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("role=\"alert\"", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await using var scope = identity.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Users
            .AnyAsync(u => u.Email == email, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Confirmation_GetOnlyRendersTheForm_PostConfirms_ReplayFails_AndSignInThenWorks()
    {
        identity.RequireAvailable();
        var email = NewEmail();
        using var browser = identity.CreateBrowser();
        using (await identity.RegisterAsync(browser, email))
        {
        }

        var link = Assert.Single(await identity.Dispatcher.WaitForAsync(email)).Link;
        var page = link.PathAndQuery;

        // Opening the link, as an email scanner would, changes nothing.
        using (var preview = await browser.GetAsync(page, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.True(preview.Headers.CacheControl?.NoStore, "Sensitive pages must not be stored.");
            Assert.Equal("no-referrer", preview.Headers.GetValues("Referrer-Policy").Single());
        }

        Assert.False(await IsConfirmedAsync(email));
        Assert.Null(await ConsumedAtAsync(email));

        var token = CapturingSecurityEmailDispatcher.TokenOf(Assert.Single(identity.Dispatcher.MessagesTo(email)));
        using var confirm = await identity.SubmitPageFormAsync(browser, page, new Dictionary<string, string> { ["Token"] = token });
        var confirmBody = await confirm.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Your email address is confirmed", confirmBody, StringComparison.Ordinal);
        Assert.DoesNotContain(token, confirmBody, StringComparison.Ordinal);
        Assert.True(await IsConfirmedAsync(email));

        using var replay = await identity.SubmitPageFormAsync(browser, page, new Dictionary<string, string> { ["Token"] = token });
        Assert.Contains("invalid or has expired", await replay.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        using var login = await identity.SubmitLoginAsync(identity.CreateBrowser(), email, IdentityServerFixture.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    [Fact]
    public async Task Confirmation_WithAnExpiredToken_IsRejected()
    {
        identity.RequireAvailable();
        var (email, token) = await RegisterAndReceiveTokenAsync();
        await using (var scope = identity.Services.CreateAsyncScope())
        {
            var userId = await identity.GetUserIdAsync(email);
            await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().AccountSecurityTokens
                .Where(t => t.UserId == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), TestContext.Current.CancellationToken);
        }

        Assert.Equal(EmailConfirmationResult.InvalidOrExpired, await ConfirmAsync(token));
        Assert.False(await IsConfirmedAsync(email));
    }

    [Fact]
    public async Task ConcurrentConfirmation_HasExactlyOneWinner()
    {
        identity.RequireAvailable();
        var (email, token) = await RegisterAndReceiveTokenAsync();

        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(
            async () =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                return await ConfirmAsync(token);
            },
            TestContext.Current.CancellationToken)).ToArray();
        start.Set();

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, result => result == EmailConfirmationResult.Confirmed);
        Assert.True(await IsConfirmedAsync(email));
    }

    [Fact]
    public async Task Confirmation_WhenTheAccountUpdateFails_RollsBackTheTokenConsumption()
    {
        identity.RequireAvailable();
        var (email, token) = await RegisterAndReceiveTokenAsync();
        var userId = await identity.GetUserIdAsync(email);
        await SetStatusAsync(userId, AccountStatus.Disabled);

        Assert.Equal(EmailConfirmationResult.InvalidOrExpired, await ConfirmAsync(token));
        Assert.Null(await ConsumedAtAsync(email));

        // Once the account is active again, the same, still unconsumed token works.
        await SetStatusAsync(userId, AccountStatus.Active);
        Assert.Equal(EmailConfirmationResult.Confirmed, await ConfirmAsync(token));
    }

    [Fact]
    public async Task Resend_IsThrottled_AndANewTokenSupersedesTheOldOne()
    {
        identity.RequireAvailable();
        var (email, firstToken) = await RegisterAndReceiveTokenAsync();
        var userId = await identity.GetUserIdAsync(email);
        using var browser = identity.CreateBrowser();

        // Within 60 seconds of the first email: suppressed, but the response is the same neutral page.
        using (var suppressed = await ResendAsync(browser, email))
        {
            Assert.Equal("/account/check-email", suppressed.Headers.Location?.OriginalString);
        }

        Assert.Equal(1, await DeliveryCountAsync(userId));

        await AgeDeliveriesAsync(userId, TimeSpan.FromMinutes(2));
        using (await ResendAsync(browser, email))
        {
        }

        var secondToken = CapturingSecurityEmailDispatcher.TokenOf((await identity.Dispatcher.WaitForAsync(email, 2))[1]);
        Assert.Equal(2, await DeliveryCountAsync(userId));
        Assert.Equal(EmailConfirmationResult.InvalidOrExpired, await ConfirmAsync(firstToken));
        Assert.Equal(EmailConfirmationResult.Confirmed, await ConfirmAsync(secondToken));
    }

    [Fact]
    public async Task Resend_AfterThreeEmailsInAnHour_IsSuppressed()
    {
        identity.RequireAvailable();
        var (email, _) = await RegisterAndReceiveTokenAsync();
        var userId = await identity.GetUserIdAsync(email);
        using var browser = identity.CreateBrowser();

        for (var i = 0; i < 2; i++)
        {
            await AgeDeliveriesAsync(userId, TimeSpan.FromMinutes(2));
            using (await ResendAsync(browser, email))
            {
            }
        }

        Assert.Equal(3, await DeliveryCountAsync(userId));
        await AgeDeliveriesAsync(userId, TimeSpan.FromMinutes(2));
        using (await ResendAsync(browser, email))
        {
        }

        Assert.Equal(3, await DeliveryCountAsync(userId));
    }

    [Fact]
    public async Task Resend_ForAnUnknownOrConfirmedAddress_LooksIdentical_AndSendsNothing()
    {
        identity.RequireAvailable();
        var confirmed = await identity.CreateUserAsync();
        var unknown = NewEmail();
        using var browser = identity.CreateBrowser();

        using var unknownResponse = await ResendAsync(browser, unknown);
        using var confirmedResponse = await ResendAsync(browser, confirmed);

        Assert.Equal("/account/check-email", unknownResponse.Headers.Location?.OriginalString);
        Assert.Equal("/account/check-email", confirmedResponse.Headers.Location?.OriginalString);
        Assert.Empty(identity.Dispatcher.MessagesTo(unknown));
        Assert.Equal(0, await DeliveryCountAsync(await identity.GetUserIdAsync(confirmed)));
    }

    private static string NewEmail() => $"reg-{Guid.NewGuid():N}@example.test";

    private async Task<(string Email, string Token)> RegisterAndReceiveTokenAsync()
    {
        var email = NewEmail();
        using var browser = identity.CreateBrowser();
        using (await identity.RegisterAsync(browser, email))
        {
        }

        var message = Assert.Single(await identity.Dispatcher.WaitForAsync(email));
        return (email, CapturingSecurityEmailDispatcher.TokenOf(message));
    }

    private Task<HttpResponseMessage> ResendAsync(HttpClient browser, string email) =>
        identity.SubmitPageFormAsync(browser, "/account/resend-confirmation", new Dictionary<string, string> { ["Email"] = email });

    private async Task<EmailConfirmationResult> ConfirmAsync(string token)
    {
        await using var scope = identity.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ConfirmEmailHandler>().HandleAsync(token, TestContext.Current.CancellationToken);
    }

    private async Task<bool> IsConfirmedAsync(string email)
    {
        await using var scope = identity.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Users
            .Where(u => u.NormalizedEmail == email.ToUpperInvariant())
            .Select(u => u.EmailConfirmed)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<DateTimeOffset?> ConsumedAtAsync(string email)
    {
        var userId = await identity.GetUserIdAsync(email);
        await using var scope = identity.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().AccountSecurityTokens
            .Where(t => t.UserId == userId)
            .Select(t => t.ConsumedAt)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task SetStatusAsync(string userId, AccountStatus status)
    {
        await using var scope = identity.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(u => u.Status, status), TestContext.Current.CancellationToken);
    }

    private async Task<int> DeliveryCountAsync(string userId)
    {
        await using var scope = identity.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().SecurityEmailDeliveries
            .CountAsync(d => d.UserId == userId, TestContext.Current.CancellationToken);
    }

    /// <summary>Moves earlier deliveries back in time, as if the user waited.</summary>
    private async Task AgeDeliveriesAsync(string userId, TimeSpan age)
    {
        await using var scope = identity.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().SecurityEmailDeliveries
            .Where(d => d.UserId == userId)
            .ExecuteUpdateAsync(set => set.SetProperty(d => d.CreatedAt, d => d.CreatedAt.AddSeconds(-age.TotalSeconds)), TestContext.Current.CancellationToken);
    }

    private static async Task<SecurityEmailDeliveryRecord> WaitForFinalDeliveryAsync(IdentityServiceDbContext context, string userId)
    {
        for (var i = 0; i < 200; i++)
        {
            var delivery = await context.SecurityEmailDeliveries.AsNoTracking().SingleAsync(d => d.UserId == userId, TestContext.Current.CancellationToken);
            if (SecurityEmailDeliveryTransitions.IsTerminal(delivery.Status))
            {
                return delivery;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The delivery did not reach a final status.");
    }
}
