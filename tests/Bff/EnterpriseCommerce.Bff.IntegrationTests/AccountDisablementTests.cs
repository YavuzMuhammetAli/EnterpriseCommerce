using System.Net;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Domain.Accounts;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>
/// Account disablement seen from a BFF (ADR-017 sections 6 and 7, Phase 2.3 S6): the BFF session ends at its next
/// refresh, the old refresh token is never presented again, and silent re-authorization cannot revive the session.
/// </summary>
public sealed class AccountDisablementTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task AfterDisablement_TheBffSessionEndsAtItsNextRefresh_AndCannotReauthorizeSilently()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        var userId = await environment.UserIdAsync(email);
        await environment.SignInAsync(browser, bff, email);
        var record = bff.RecordIdOf(browser)!;
        var version = await environment.SecurityVersionAsync(userId);

        await using (var scope = environment.Identity.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<DisableAccountHandler>().HandleAsync(
                new AccountStatusChange(userId, AccountStatusReason.SecurityConcern, "admin:test", "svc-admin"),
                TestContext.Current.CancellationToken);
            Assert.Equal(AccountLifecycleOutcome.Changed, result.Outcome);
        }

        Assert.Equal(version + 1, await environment.SecurityVersionAsync(userId));

        // The access token is still valid, so the session is unchanged until a refresh is due (ADR-017 section 6).
        Assert.Equal(HttpStatusCode.OK, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
        Assert.Equal(0, bff.Channel.RefreshRequests);

        // The next refresh is refused: the session is deleted, the refresh token was presented exactly once.
        bff.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(HttpStatusCode.Unauthorized, (await BffTestEnvironment.UserAsync(browser, bff)).User.Status);
        Assert.Equal(1, bff.Channel.RefreshRequests);
        Assert.Null(await bff.Store.GetTokensAsync(record, TestContext.Current.CancellationToken));

        // The Identity SSO cookie no longer passes either: silent re-authorization falls back to the login page,
        // where the disabled account cannot sign in.
        var landing = await browser.NavigateAsync(bff.Url("/bff/login?silent=1&returnUrl=%2Fhome"));
        Assert.Equal("/account/login", landing.Url.AbsolutePath);
        Assert.Null(bff.RecordIdOf(browser));
    }
}
