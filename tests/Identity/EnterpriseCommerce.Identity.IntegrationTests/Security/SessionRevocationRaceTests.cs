using System.Net;
using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Identity.IntegrationTests.Protocol;

namespace EnterpriseCommerce.Identity.IntegrationTests.Security;

/// <summary>
/// Password reset and logout-all concurrent with a refresh of an existing session (PHASE_2_2_AUTHENTICATION_FLOWS.md
/// section 11.5, ADR-017 section 8), against a real SQL Server.
/// </summary>
/// <remarks>
/// Both orderings are forced, not hoped for. One operation is held after its work and before its commit, with its
/// locks held, by a commit gate. The other is then started, and the test proves through
/// <c>sys.dm_exec_requests</c> that it is blocked by the first one before the gate opens. No delay implies an order.
/// </remarks>
public sealed class SessionRevocationRaceTests(ControlledIdentityServerFixture identity) : IClassFixture<ControlledIdentityServerFixture>
{
    private const string RefreshOperation = "token-grant-refresh";

    [Fact]
    public async Task PasswordReset_WhenRefreshCommitsFirst_TheRefreshedSuccessorIsUnusable()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync(withResetToken: true);

        TokenResponse refreshed;
        string resetPage;
        using (var gate = identity.Transactions.HoldBeforeCommit(RefreshOperation))
        {
            var refresh = Task.Run(() => identity.RefreshAsync(identity.Storefront, arranged.RefreshToken), TestContext.Current.CancellationToken);
            await gate.Reached;

            var reset = Task.Run(() => ResetAsync(arranged.ResetToken!), TestContext.Current.CancellationToken);

            // The refresh holds its shared lock on the security version: the reset cannot commit before it.
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(reset.IsCompleted);

            gate.Open();
            refreshed = await refresh;
            resetPage = await reset;
        }

        // Case A: the refresh committed first and was answered. The reset committed afterwards.
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.NotNull(refreshed.RefreshToken);
        Assert.Contains("Your password has been changed", resetPage, StringComparison.Ordinal);

        // The successor issued just before the reset cannot produce a further token.
        await AssertRefreshDeniedAsync(refreshed.RefreshToken!);
        await AssertResetCommittedAsync(arranged);
    }

    [Fact]
    public async Task PasswordReset_WhenResetCommitsFirst_TheRefreshIsDeniedAndIssuesNothing()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync(withResetToken: true);

        TokenResponse refreshed;
        string resetPage;
        using (var gate = identity.Transactions.HoldBeforeCommit(ResetPasswordHandler.Operation))
        {
            var reset = Task.Run(() => ResetAsync(arranged.ResetToken!), TestContext.Current.CancellationToken);
            await gate.Reached;

            var refresh = Task.Run(() => identity.RefreshAsync(identity.Storefront, arranged.RefreshToken), TestContext.Current.CancellationToken);

            // The uncommitted reset holds the account and security-version rows: the refresh waits for it.
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(refresh.IsCompleted);

            gate.Open();
            resetPage = await reset;
            refreshed = await refresh;
        }

        // Case B: the reset committed first. The refresh saw the new security state and was refused.
        Assert.Contains("Your password has been changed", resetPage, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, refreshed.Status);
        Assert.Equal("invalid_grant", refreshed.Error);
        Assert.Null(refreshed.AccessToken);
        Assert.Null(refreshed.RefreshToken);

        await AssertResetCommittedAsync(arranged);
    }

    [Fact]
    public async Task LogoutAll_WhenRefreshCommitsFirst_TheRefreshedSuccessorIsUnusable()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync(withResetToken: false);

        TokenResponse refreshed;
        HttpStatusCode logoutAll;
        using (var gate = identity.Transactions.HoldBeforeCommit(RefreshOperation))
        {
            var refresh = Task.Run(() => identity.RefreshAsync(identity.Storefront, arranged.RefreshToken), TestContext.Current.CancellationToken);
            await gate.Reached;

            var logout = Task.Run(() => identity.LogoutAllAsync(arranged.AccessToken), TestContext.Current.CancellationToken);
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(logout.IsCompleted);

            gate.Open();
            refreshed = await refresh;
            logoutAll = await logout;
        }

        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal(HttpStatusCode.NoContent, logoutAll);
        await AssertRefreshDeniedAsync(refreshed.RefreshToken!);
        await AssertRefreshDeniedAsync(arranged.RefreshToken);
        Assert.Equal(arranged.Version + 1, await identity.SecurityVersionOfAsync(arranged.UserId));
        Assert.False(await SignedInWithAsync(arranged, arranged.OldCookies));
    }

    [Fact]
    public async Task LogoutAll_WhenLogoutCommitsFirst_TheRefreshIsDeniedAndIssuesNothing()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync(withResetToken: false);

        TokenResponse refreshed;
        HttpStatusCode logoutAll;
        using (var gate = identity.Transactions.HoldBeforeCommit("logout-all"))
        {
            var logout = Task.Run(() => identity.LogoutAllAsync(arranged.AccessToken), TestContext.Current.CancellationToken);
            await gate.Reached;

            var refresh = Task.Run(() => identity.RefreshAsync(identity.Storefront, arranged.RefreshToken), TestContext.Current.CancellationToken);
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(refresh.IsCompleted);

            gate.Open();
            logoutAll = await logout;
            refreshed = await refresh;
        }

        Assert.Equal(HttpStatusCode.NoContent, logoutAll);
        Assert.Equal(HttpStatusCode.BadRequest, refreshed.Status);
        Assert.Null(refreshed.AccessToken);
        Assert.Null(refreshed.RefreshToken);
        Assert.Equal(arranged.Version + 1, await identity.SecurityVersionOfAsync(arranged.UserId));
        await AssertRefreshDeniedAsync(arranged.RefreshToken);
    }

    /// <summary>A confirmed user with a browser SSO session, a storefront session (refresh chain) and, optionally, a reset token.</summary>
    private async Task<Arranged> ArrangeAsync(bool withResetToken)
    {
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var jar = new CookieJar();
        using var browser = identity.CreateBrowser(jar);

        var session = await identity.SignInAsync(identity.Storefront, email, browser);
        Assert.Equal(HttpStatusCode.OK, session.Status);
        Assert.True(await identity.IsSignedInAsync(browser));

        var resetToken = withResetToken ? await identity.RequestResetTokenAsync(email) : null;
        return new Arranged(email, userId, session.AccessToken!, session.RefreshToken!, resetToken, jar.Snapshot(), (await identity.SecurityVersionOfAsync(userId))!.Value);
    }

    private async Task<string> ResetAsync(string token)
    {
        using var browser = identity.CreateBrowser();
        using var response = await identity.SubmitResetAsync(browser, token);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertRefreshDeniedAsync(string refreshToken)
    {
        var response = await identity.RefreshAsync(identity.Storefront, refreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalid_grant", response.Error);
        Assert.Null(response.RefreshToken);
    }

    private async Task<bool> SignedInWithAsync(Arranged arranged, IReadOnlyDictionary<string, string> cookies)
    {
        var jar = new CookieJar();
        jar.Restore(cookies);
        using var browser = identity.CreateBrowser(jar);
        return await identity.IsSignedInAsync(browser);
    }

    /// <summary>Everything the reset decided is committed together, and nothing from before it is usable.</summary>
    private async Task AssertResetCommittedAsync(Arranged arranged)
    {
        Assert.Equal(arranged.Version + 1, await identity.SecurityVersionOfAsync(arranged.UserId));
        Assert.True(await identity.IsConsumedAsync(arranged.ResetToken!));
        Assert.True(await identity.PasswordWorksAsync(arranged.Email, IdentityServerFixture.NewPassword));
        Assert.False(await identity.PasswordWorksAsync(arranged.Email, IdentityServerFixture.Password));

        // The old chain, the old browser session and the consumed token are all unusable.
        await AssertRefreshDeniedAsync(arranged.RefreshToken);
        Assert.False(await SignedInWithAsync(arranged, arranged.OldCookies));
        Assert.Contains("invalid or has expired", await ResetAsync(arranged.ResetToken!), StringComparison.Ordinal);
    }

    private sealed record Arranged(
        string Email,
        string UserId,
        string AccessToken,
        string RefreshToken,
        string? ResetToken,
        IReadOnlyDictionary<string, string> OldCookies,
        long Version);
}
