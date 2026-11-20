using System.Net;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// The internal account lifecycle API (IDENTITY_API_CONTRACT.md section 2.12): the service-token authorization boundary,
/// input validation, and the end-to-end effect of disablement and reactivation on every kind of session.
/// </summary>
public sealed class AccountLifecycleApiTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    [Fact]
    public async Task OnlyAnAllowListedServiceClientWithTheScope_MayChangeAccountStatus()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var userToken = (await identity.SignInAsync(identity.Storefront, email)).AccessToken!;
        var otherServiceToken = await identity.ServiceTokenAsync(identity.Service, "svc.inventory.reservations");
        var rogueToken = await identity.ServiceTokenAsync(identity.RogueService);
        var adminToken = await identity.ServiceTokenAsync(identity.AccountAdmin);

        Assert.Equal(HttpStatusCode.Unauthorized, (await identity.CallAccountLifecycleAsync("disable", userId, bearerToken: null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await identity.CallAccountLifecycleAsync("disable", userId, "not-a-token")).Status);

        // A user token (front-end audience) is a valid token for Identity, but never an administrative caller.
        Assert.Equal(HttpStatusCode.Forbidden, (await identity.CallAccountLifecycleAsync("disable", userId, userToken)).Status);

        // A token for another service's audience is not a token for Identity at all.
        Assert.Equal(HttpStatusCode.Unauthorized, (await identity.CallAccountLifecycleAsync("disable", userId, otherServiceToken)).Status);

        // The right scope and audience, from a service client that is not on the allow-list.
        Assert.Equal(HttpStatusCode.Forbidden, (await identity.CallAccountLifecycleAsync("disable", userId, rogueToken)).Status);
        Assert.Equal(AccountStatus.Active, (await identity.ReadUserAsync(userId)).Status);

        // The administrative service token cannot be used on the user endpoints either.
        Assert.Equal(HttpStatusCode.Forbidden, await identity.LogoutAllAsync(adminToken));

        var allowed = await identity.CallAccountLifecycleAsync("disable", userId, adminToken);
        Assert.Equal(HttpStatusCode.OK, allowed.Status);
        Assert.Equal(AccountStatus.Disabled, (await identity.ReadUserAsync(userId)).Status);
    }

    [Fact]
    public async Task InvalidRequests_AreRejectedAsProblems_AndUnknownAccountsAreNotFound()
    {
        identity.RequireAvailable();
        var userId = await identity.GetUserIdAsync(await identity.CreateUserAsync());
        var token = await identity.ServiceTokenAsync(identity.AccountAdmin);

        foreach (var body in new object[]
        {
            new { actorReference = "admin:alice" },
            new { reason = "Whatever", actorReference = "admin:alice" },
            new { reason = "1", actorReference = "admin:alice" },
            new { reason = "Administrative" },
            new { reason = "Administrative", actorReference = "admin alice <script>" },
            new { reason = "Administrative", actorReference = new string('a', AccountLifecycle.ActorReferenceMaxLength + 1) },
        })
        {
            var (status, problem) = await identity.CallAccountLifecycleAsync("disable", userId, token, body);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(problem.TryGetProperty("errors", out _));
        }

        Assert.Equal(AccountStatus.Active, (await identity.ReadUserAsync(userId)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await identity.CallAccountLifecycleAsync("disable", Guid.NewGuid().ToString(), token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await identity.CallAccountLifecycleAsync("reactivate", Guid.NewGuid().ToString(), token)).Status);
    }

    [Fact]
    public async Task Disable_EndsEverySessionAndRefusesSignIn_WithoutRevealingTheStatus()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();
        var resetToken = await identity.RequestResetTokenAsync(arranged.Email);

        var (status, body) = await identity.DisableThroughApiAsync(arranged.UserId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(arranged.UserId, body.GetProperty("accountId").GetString());
        Assert.Equal("Disabled", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.Equal(arranged.Version + 1, await identity.SecurityVersionOfAsync(arranged.UserId));

        // Refresh chain, SSO cookie and a correct password: all refused, the password with the generic failure.
        await AssertRefreshDeniedAsync(arranged.RefreshToken);
        Assert.False(await SignedInWithAsync(arranged.Cookies));
        using (var browser = identity.CreateBrowser())
        {
            using var login = await identity.SubmitLoginAsync(browser, arranged.Email, IdentityServerFixture.Password);
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            Assert.Contains("Invalid email or password", await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        // The emailed reset link from before the disablement was superseded.
        using (var browser = identity.CreateBrowser())
        {
            using var reset = await identity.SubmitResetAsync(browser, resetToken);
            Assert.Contains("invalid or has expired", await reset.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }

        Assert.All(await identity.AuthorizationStatusesAsync(arranged.UserId), s => Assert.Equal(OpenIddictConstants.Statuses.Revoked, s));

        // Repeating the request is harmless and reports that nothing changed.
        var (repeatStatus, repeatBody) = await identity.DisableThroughApiAsync(arranged.UserId);
        Assert.Equal(HttpStatusCode.OK, repeatStatus);
        Assert.False(repeatBody.GetProperty("changed").GetBoolean());
        Assert.Equal(arranged.Version + 1, await identity.SecurityVersionOfAsync(arranged.UserId));
    }

    [Fact]
    public async Task Reactivate_RestoresNoSession_AndTheUserSignsInAgain()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();
        Assert.Equal(HttpStatusCode.OK, (await identity.DisableThroughApiAsync(arranged.UserId)).Status);

        var (status, body) = await identity.ReactivateThroughApiAsync(arranged.UserId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("changed").GetBoolean());
        Assert.Equal(arranged.Version + 1, await identity.SecurityVersionOfAsync(arranged.UserId));

        // Nothing from before the disablement comes back.
        await AssertRefreshDeniedAsync(arranged.RefreshToken);
        Assert.False(await SignedInWithAsync(arranged.Cookies));

        // A new sign-in works and starts a new session at the raised version.
        var session = await identity.SignInAsync(identity.Storefront, arranged.Email);
        Assert.Equal(HttpStatusCode.OK, session.Status);
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
    }

    private async Task<Arranged> ArrangeAsync()
    {
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var jar = new CookieJar();
        using var browser = identity.CreateBrowser(jar);
        var session = await identity.SignInAsync(identity.Storefront, email, browser);
        Assert.Equal(HttpStatusCode.OK, session.Status);
        Assert.True(await identity.IsSignedInAsync(browser));
        return new Arranged(email, userId, session.RefreshToken!, jar.Snapshot(), (await identity.SecurityVersionOfAsync(userId))!.Value);
    }

    private async Task AssertRefreshDeniedAsync(string refreshToken)
    {
        var response = await identity.RefreshAsync(identity.Storefront, refreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalid_grant", response.Error);
        Assert.Null(response.RefreshToken);
    }

    private async Task<bool> SignedInWithAsync(IReadOnlyDictionary<string, string> cookies)
    {
        var jar = new CookieJar();
        jar.Restore(cookies);
        using var browser = identity.CreateBrowser(jar);
        return await identity.IsSignedInAsync(browser);
    }

    private sealed record Arranged(string Email, string UserId, string RefreshToken, IReadOnlyDictionary<string, string> Cookies, long Version);
}
