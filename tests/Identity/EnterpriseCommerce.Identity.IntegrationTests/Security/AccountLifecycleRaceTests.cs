using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Identity.IntegrationTests.Protocol;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.IntegrationTests.Security;

/// <summary>
/// Account disablement and reactivation concurrent with refresh, code exchange, password reset, a second disablement
/// and login, against a real SQL Server (ADR-017 sections 6 to 8).
/// </summary>
/// <remarks>
/// Every ordering is forced, as in <see cref="SessionRevocationRaceTests"/>: one operation is held after its work and
/// before its commit (keeping its locks), the other is started, and the test proves through <c>sys.dm_exec_requests</c>
/// that it is blocked before the gate opens. The invariant in every case: no disabled account ends up with a usable
/// refresh token or Identity session.
/// </remarks>
public sealed class AccountLifecycleRaceTests(ControlledIdentityServerFixture identity) : IClassFixture<ControlledIdentityServerFixture>
{
    private const string RefreshOperation = "token-grant-refresh";
    private const string CodeOperation = "token-grant-code";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disable_WhenRefreshCommitsFirst_TheRefreshedSuccessorIsUnusable()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();

        TokenResponse refreshed;
        HttpStatusCode disabled;
        using (var gate = identity.Transactions.HoldBeforeCommit(RefreshOperation))
        {
            var refresh = Task.Run(() => identity.RefreshAsync(identity.Storefront, arranged.RefreshToken), Ct);
            await gate.Reached;

            var disable = Task.Run(() => DisableAsync(arranged), Ct);

            // The refresh holds its shared lock on the security version: the disablement cannot commit before it.
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(disable.IsCompleted);

            gate.Open();
            refreshed = await refresh;
            disabled = await disable;
        }

        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal(HttpStatusCode.OK, disabled);
        await AssertNothingUsableAsync(arranged, refreshed.RefreshToken!);
    }

    [Fact]
    public async Task Disable_WhenDisableCommitsFirst_TheRefreshIsDeniedAndIssuesNothing()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();

        TokenResponse refreshed;
        HttpStatusCode disabled;
        using (var gate = identity.Transactions.HoldBeforeCommit(DisableAccountHandler.Operation))
        {
            var disable = Task.Run(() => DisableAsync(arranged), Ct);
            await gate.Reached;

            var refresh = Task.Run(() => identity.RefreshAsync(identity.Storefront, arranged.RefreshToken), Ct);

            // The uncommitted disablement holds the security-version row: the refresh decision waits for it.
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(refresh.IsCompleted);

            gate.Open();
            disabled = await disable;
            refreshed = await refresh;
        }

        Assert.Equal(HttpStatusCode.OK, disabled);
        AssertDenied(refreshed);
        await AssertNothingUsableAsync(arranged, refreshToken: null);
    }

    [Fact]
    public async Task Disable_WhenTheCodeExchangeCommitsFirst_TheNewSessionCannotRefresh()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();
        var (code, verifier) = await AuthorizeAgainAsync(arranged);

        TokenResponse exchanged;
        HttpStatusCode disabled;
        using (var gate = identity.Transactions.HoldBeforeCommit(CodeOperation))
        {
            var exchange = Task.Run(() => identity.ExchangeCodeAsync(identity.Storefront, code, verifier), Ct);
            await gate.Reached;

            var disable = Task.Run(() => DisableAsync(arranged), Ct);
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(disable.IsCompleted);

            gate.Open();
            exchanged = await exchange;
            disabled = await disable;
        }

        // The login was ordered before the disablement. Its tokens were issued, but its refresh token is dead.
        Assert.Equal(HttpStatusCode.OK, exchanged.Status);
        Assert.Equal(HttpStatusCode.OK, disabled);
        await AssertNothingUsableAsync(arranged, exchanged.RefreshToken!);
    }

    [Fact]
    public async Task Disable_WhenDisableCommitsFirst_TheCodeExchangeIsDenied()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();
        var (code, verifier) = await AuthorizeAgainAsync(arranged);

        TokenResponse exchanged;
        HttpStatusCode disabled;
        using (var gate = identity.Transactions.HoldBeforeCommit(DisableAccountHandler.Operation))
        {
            var disable = Task.Run(() => DisableAsync(arranged), Ct);
            await gate.Reached;

            var exchange = Task.Run(() => identity.ExchangeCodeAsync(identity.Storefront, code, verifier), Ct);
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(exchange.IsCompleted);

            gate.Open();
            disabled = await disable;
            exchanged = await exchange;
        }

        Assert.Equal(HttpStatusCode.OK, disabled);
        AssertDenied(exchanged);
        Assert.Null(exchanged.AccessToken);
        await AssertNothingUsableAsync(arranged, refreshToken: null);
    }

    [Fact]
    public async Task Disable_WhenDisableCommitsFirst_APendingPasswordResetIsRejected()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();
        var resetToken = await identity.RequestResetTokenAsync(arranged.Email);

        string resetPage;
        HttpStatusCode disabled;
        using (var gate = identity.Transactions.HoldBeforeCommit(DisableAccountHandler.Operation))
        {
            var disable = Task.Run(() => DisableAsync(arranged), Ct);
            await gate.Reached;

            // The disablement superseded the reset token and holds its row: the consumption waits for it.
            var reset = Task.Run(() => ResetAsync(resetToken), Ct);
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(reset.IsCompleted);

            gate.Open();
            disabled = await disable;
            resetPage = await reset;
        }

        Assert.Equal(HttpStatusCode.OK, disabled);
        Assert.Contains("invalid or has expired", resetPage, StringComparison.Ordinal);
        Assert.False(await identity.IsConsumedAsync(resetToken));
        Assert.Equal(arranged.Version + 1, await identity.SecurityVersionOfAsync(arranged.UserId));
        await AssertNothingUsableAsync(arranged, refreshToken: null);
    }

    [Fact]
    public async Task Disable_WhenAPasswordResetCommitsFirst_TheAccountStillEndsDisabled()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();
        var resetToken = await identity.RequestResetTokenAsync(arranged.Email);

        string resetPage;
        HttpStatusCode disabled;
        using (var gate = identity.Transactions.HoldBeforeCommit(ResetPasswordHandler.Operation))
        {
            var reset = Task.Run(() => ResetAsync(resetToken), Ct);
            await gate.Reached;

            // The uncommitted reset holds the account row: the status change waits for it.
            var disable = Task.Run(() => DisableAsync(arranged), Ct);
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(disable.IsCompleted);

            gate.Open();
            resetPage = await reset;
            disabled = await disable;
        }

        Assert.Contains("Your password has been changed", resetPage, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, disabled);
        Assert.Equal(arranged.Version + 2, await identity.SecurityVersionOfAsync(arranged.UserId));

        // The new password does not open a disabled account.
        Assert.False(await identity.PasswordWorksAsync(arranged.Email, IdentityServerFixture.NewPassword));
        await AssertNothingUsableAsync(arranged, refreshToken: null);
    }

    [Fact]
    public async Task Disable_TwoConcurrentRequests_ChangeTheAccountOnceAndRaiseTheVersionOnce()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();

        (HttpStatusCode Status, JsonElement Body) first;
        (HttpStatusCode Status, JsonElement Body) second;
        using (var gate = identity.Transactions.HoldBeforeCommit(DisableAccountHandler.Operation))
        {
            var one = Task.Run(() => identity.CallAccountLifecycleAsync("disable", arranged.UserId, arranged.AdminToken), Ct);
            await gate.Reached;

            var two = Task.Run(() => identity.CallAccountLifecycleAsync("disable", arranged.UserId, arranged.AdminToken), Ct);
            await SqlBlocking.WaitUntilBlockedAsync(identity.ConnectionString);
            Assert.False(two.IsCompleted);

            gate.Open();
            first = await one;
            second = await two;
        }

        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.True(first.Body.GetProperty("changed").GetBoolean());
        Assert.False(second.Body.GetProperty("changed").GetBoolean());
        Assert.Equal(arranged.Version + 1, await identity.SecurityVersionOfAsync(arranged.UserId));
        await AssertNothingUsableAsync(arranged, refreshToken: null);
    }

    [Fact]
    public async Task Reactivate_WhileUncommitted_LoginIsRefused_AndAfterCommitItSucceeds()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();
        Assert.Equal(HttpStatusCode.OK, await DisableAsync(arranged));

        using (var gate = identity.Transactions.HoldBeforeCommit(ReactivateAccountHandler.Operation))
        {
            var reactivate = Task.Run(() => identity.CallAccountLifecycleAsync("reactivate", arranged.UserId, arranged.AdminToken), Ct);
            await gate.Reached;

            // A sign-in during the uncommitted reactivation reads the committed (disabled) status and fails generically.
            Assert.False(await identity.PasswordWorksAsync(arranged.Email, IdentityServerFixture.Password));

            gate.Open();
            Assert.Equal(HttpStatusCode.OK, (await reactivate).Status);
        }

        Assert.True(await identity.PasswordWorksAsync(arranged.Email, IdentityServerFixture.Password));
        AssertDenied(await identity.RefreshAsync(identity.Storefront, arranged.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, (await identity.SignInAsync(identity.Storefront, arranged.Email)).Status);
    }

    [Fact]
    public async Task Disable_WhenCleanupFails_TheDecisionStands_AndARepeatCompletesTheCleanup()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();

        identity.Cleanup.Fail = true;
        (HttpStatusCode Status, JsonElement Body) result;
        try
        {
            result = await identity.CallAccountLifecycleAsync("disable", arranged.UserId, arranged.AdminToken);
        }
        finally
        {
            identity.Cleanup.Fail = false;
        }

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.True(result.Body.GetProperty("changed").GetBoolean());

        // The OpenIddict entries were not revoked, yet the committed status and version refuse the session.
        Assert.All(await identity.AuthorizationStatusesAsync(arranged.UserId), s => Assert.Equal(OpenIddictConstants.Statuses.Valid, s));
        Assert.False(await SignedInWithAsync(arranged.Cookies));

        var repeat = await identity.CallAccountLifecycleAsync("disable", arranged.UserId, arranged.AdminToken);
        Assert.Equal(HttpStatusCode.OK, repeat.Status);
        Assert.False(repeat.Body.GetProperty("changed").GetBoolean());
        Assert.All(await identity.AuthorizationStatusesAsync(arranged.UserId), s => Assert.Equal(OpenIddictConstants.Statuses.Revoked, s));
        AssertDenied(await identity.RefreshAsync(identity.Storefront, arranged.RefreshToken));
    }

    [Fact]
    public async Task Disable_WhenTheTransactionIsUnavailable_Answers503AndChangesNothing()
    {
        identity.RequireAvailable();
        var arranged = await ArrangeAsync();

        using (identity.Transactions.FailOperation(DisableAccountHandler.Operation))
        {
            var (status, _) = await identity.CallAccountLifecycleAsync("disable", arranged.UserId, arranged.AdminToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        }

        Assert.Equal(AccountStatus.Active, (await identity.ReadUserAsync(arranged.UserId)).Status);
        Assert.Equal(arranged.Version, await identity.SecurityVersionOfAsync(arranged.UserId));
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Storefront, arranged.RefreshToken)).Status);
    }

    /// <summary>A confirmed user with an SSO browser session, a storefront session, and an admin service token.</summary>
    private async Task<Arranged> ArrangeAsync()
    {
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);
        var jar = new CookieJar();
        using var browser = identity.CreateBrowser(jar);
        var session = await identity.SignInAsync(identity.Storefront, email, browser);
        Assert.Equal(HttpStatusCode.OK, session.Status);

        // Obtained up front, so that no token request runs while a test holds a transaction open.
        var adminToken = await identity.ServiceTokenAsync(identity.AccountAdmin);
        return new Arranged(email, userId, session.RefreshToken!, jar.Snapshot(), adminToken, (await identity.SecurityVersionOfAsync(userId))!.Value);
    }

    /// <summary>A second authorization through the still-valid SSO cookie, returning an unredeemed code.</summary>
    private async Task<(string Code, string Verifier)> AuthorizeAgainAsync(Arranged arranged)
    {
        var jar = new CookieJar();
        jar.Restore(arranged.Cookies);
        using var browser = identity.CreateBrowser(jar);
        return await identity.AuthorizeAsync(browser, identity.Storefront, arranged.Email);
    }

    private async Task<HttpStatusCode> DisableAsync(Arranged arranged) =>
        (await identity.CallAccountLifecycleAsync("disable", arranged.UserId, arranged.AdminToken)).Status;

    private async Task<string> ResetAsync(string token)
    {
        using var browser = identity.CreateBrowser();
        using var response = await identity.SubmitResetAsync(browser, token);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    /// <summary>The account is disabled and nothing from any session is usable.</summary>
    private async Task AssertNothingUsableAsync(Arranged arranged, string? refreshToken)
    {
        Assert.Equal(AccountStatus.Disabled, (await identity.ReadUserAsync(arranged.UserId)).Status);
        AssertDenied(await identity.RefreshAsync(identity.Storefront, arranged.RefreshToken));
        if (refreshToken is not null)
        {
            AssertDenied(await identity.RefreshAsync(identity.Storefront, refreshToken));
        }

        Assert.False(await SignedInWithAsync(arranged.Cookies));
        Assert.False(await identity.PasswordWorksAsync(arranged.Email, IdentityServerFixture.Password));
    }

    private static void AssertDenied(TokenResponse response)
    {
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

    private sealed record Arranged(
        string Email,
        string UserId,
        string RefreshToken,
        IReadOnlyDictionary<string, string> Cookies,
        string AdminToken,
        long Version);
}
