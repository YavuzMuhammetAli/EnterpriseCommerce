using System.Net;
using EnterpriseCommerce.Identity.Application.Sessions;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Refresh-token rotation, the redemption ledger and the reuse policy through the real token endpoint
/// (ADR-017 sections 5 to 8; PoC-1, PoC-3, PoC-13).
/// </summary>
public sealed class RefreshTokenSecurityTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    [Fact]
    public async Task ConcurrentRefresh_WithTheSameToken_HasExactlyOneWinner_AndEndsTheSession()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var session = await identity.SignInAsync(identity.Storefront, email);
        var authorizationId = await AuthorizationIdAsync(email, identity.Storefront);

        using var start = new ManualResetEventSlim(false);
        var attempts = Enumerable.Range(0, 8).Select(_ => Task.Run(
            async () =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                return await identity.RefreshAsync(identity.Storefront, session.RefreshToken!);
            },
            TestContext.Current.CancellationToken)).ToArray();
        start.Set();
        var results = await Task.WhenAll(attempts);

        var winners = results.Where(r => r.Status == HttpStatusCode.OK).ToList();
        Assert.Single(winners);
        Assert.All(results.Where(r => r.Status != HttpStatusCode.OK), r => Assert.Equal("invalid_grant", r.Error));

        // The losers count as reuse: the session is revoked, so even the winner's successor is unusable (no fork).
        var successor = await identity.RefreshAsync(identity.Storefront, winners[0].RefreshToken!);
        Assert.Equal(HttpStatusCode.BadRequest, successor.Status);
        Assert.Equal(1, await LedgerEntriesAsync(authorizationId));
        Assert.Equal(OpenIddictConstants.Statuses.Revoked, await AuthorizationStatusAsync(authorizationId));
    }

    [Fact]
    public async Task Reuse_ByTheCorrectPresenter_RevokesOnlyThatSession()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var storefront = await identity.SignInAsync(identity.Storefront, email);
        var merchant = await identity.SignInAsync(identity.Merchant, email);

        var rotated = await identity.RefreshAsync(identity.Storefront, storefront.RefreshToken!);
        var replay = await identity.RefreshAsync(identity.Storefront, storefront.RefreshToken!);
        var successorAfterReplay = await identity.RefreshAsync(identity.Storefront, rotated.RefreshToken!);
        var otherSession = await identity.RefreshAsync(identity.Merchant, merchant.RefreshToken!);

        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        Assert.Equal("invalid_grant", replay.Error);
        Assert.Equal(HttpStatusCode.BadRequest, successorAfterReplay.Status);
        Assert.Equal(HttpStatusCode.OK, otherSession.Status);
        Assert.Equal(1, await SecurityVersionAsync(email));
    }

    [Fact]
    public async Task CrossClientReplay_IsRejected_WithoutRevokingTheVictimsSession()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var victim = await identity.SignInAsync(identity.Storefront, email);
        var rotated = await identity.RefreshAsync(identity.Storefront, victim.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, rotated.Status);

        // PoC-13e: another confidential client, with valid credentials of its own, presents the victim's tokens.
        var redeemedReplay = await identity.RefreshAsync(identity.Merchant, victim.RefreshToken!);
        var validTokenReplay = await identity.RefreshAsync(identity.Merchant, rotated.RefreshToken!);

        Assert.Equal("invalid_grant", redeemedReplay.Error);
        Assert.Equal("invalid_grant", validTokenReplay.Error);

        // The victim's session is untouched: its current refresh token still works.
        var victimContinues = await identity.RefreshAsync(identity.Storefront, rotated.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, victimContinues.Status);
        Assert.Equal(OpenIddictConstants.Statuses.Valid, await AuthorizationStatusAsync(await AuthorizationIdAsync(email, identity.Storefront)));
    }

    [Fact]
    public async Task AdminConsoleReuse_RaisesSecurityVersion_AndInvalidatesEverySession()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var admin = await identity.SignInAsync(identity.Admin, email);
        var storefront = await identity.SignInAsync(identity.Storefront, email);

        var rotated = await identity.RefreshAsync(identity.Admin, admin.RefreshToken!);
        var replay = await identity.RefreshAsync(identity.Admin, admin.RefreshToken!);

        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        Assert.Equal("invalid_grant", replay.Error);
        Assert.Equal(2, await SecurityVersionAsync(email));
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, storefront.RefreshToken!)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Admin, rotated.RefreshToken!)).Status);

        // A fresh sign-in establishes a session at the new version.
        var fresh = await identity.SignInAsync(identity.Admin, email);
        Assert.Equal(HttpStatusCode.OK, fresh.Status);
        Assert.Equal(HttpStatusCode.OK, (await identity.RefreshAsync(identity.Admin, fresh.RefreshToken!)).Status);
    }

    [Fact]
    public async Task LogoutAll_MakesEveryExistingRefreshTokenStale()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var storefront = await identity.SignInAsync(identity.Storefront, email);
        var merchant = await identity.SignInAsync(identity.Merchant, email);

        await LogoutAllAsync(email);

        Assert.Equal("invalid_grant", (await identity.RefreshAsync(identity.Storefront, storefront.RefreshToken!)).Error);
        Assert.Equal("invalid_grant", (await identity.RefreshAsync(identity.Merchant, merchant.RefreshToken!)).Error);
    }

    [Fact]
    public async Task LogoutAll_ConcurrentWithRefresh_LeavesNoUsableRefreshToken()
    {
        identity.RequireAvailable();

        for (var round = 0; round < 5; round++)
        {
            var email = await identity.CreateUserAsync();
            var session = await identity.SignInAsync(identity.Storefront, email);

            var refresh = Task.Run(() => identity.RefreshAsync(identity.Storefront, session.RefreshToken!), TestContext.Current.CancellationToken);
            var logoutAll = Task.Run(() => LogoutAllAsync(email), TestContext.Current.CancellationToken);
            await logoutAll;
            var refreshed = await refresh;

            // Whatever the interleaving, once logout-all has committed no refresh token of the user is accepted.
            var current = refreshed.Status == HttpStatusCode.OK ? refreshed.RefreshToken! : session.RefreshToken!;
            Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, current)).Status);
        }
    }

    [Fact]
    public async Task MissingSecurityVersionRecord_FailsClosed()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var session = await identity.SignInAsync(identity.Storefront, email);
        var userId = await identity.GetUserIdAsync(email);

        await using (var scope = identity.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().SecurityVersions
                .Where(record => record.UserId == userId)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("invalid_grant", (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Error);
    }

    [Fact]
    public async Task DisabledAccount_CannotRefresh()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var session = await identity.SignInAsync(identity.Storefront, email);
        var userId = await identity.GetUserIdAsync(email);

        await using (var scope = identity.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Users
                .Where(user => user.Id == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.Status, Domain.Accounts.AccountStatus.Disabled), TestContext.Current.CancellationToken);
        }

        Assert.Equal("invalid_grant", (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Error);
    }

    /// <summary>The single authorization (session) of the user for the client. Services never read OpenIddict's private claims.</summary>
    private async Task<string> AuthorizationIdAsync(string email, TestClient client)
    {
        var userId = await identity.GetUserIdAsync(email);
        await using var scope = identity.Services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var ct = TestContext.Current.CancellationToken;
        var applicationId = await applications.GetIdAsync((await applications.FindByClientIdAsync(client.ClientId, ct))!, ct);

        var ids = new List<string>();
        await foreach (var authorization in authorizations.FindAsync(userId, applicationId, status: null, type: null, scopes: null, ct))
        {
            ids.Add((await authorizations.GetIdAsync(authorization, ct))!);
        }

        return Assert.Single(ids);
    }

    private async Task LogoutAllAsync(string email)
    {
        var userId = await identity.GetUserIdAsync(email);
        await using var scope = identity.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LogoutAllSessionsHandler>().HandleAsync(userId, TestContext.Current.CancellationToken);
    }

    private async Task<long?> SecurityVersionAsync(string email)
    {
        var userId = await identity.GetUserIdAsync(email);
        await using var scope = identity.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().SecurityVersions
            .Where(record => record.UserId == userId)
            .Select(record => (long?)record.Version)
            .SingleOrDefaultAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> LedgerEntriesAsync(string authorizationId)
    {
        await using var scope = identity.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().RefreshTokenRedemptions
            .CountAsync(entry => entry.AuthorizationId == authorizationId, TestContext.Current.CancellationToken);
    }

    private async Task<string?> AuthorizationStatusAsync(string authorizationId)
    {
        await using var scope = identity.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var authorization = await manager.FindByIdAsync(authorizationId, TestContext.Current.CancellationToken);
        return await manager.GetStatusAsync(authorization!, TestContext.Current.CancellationToken);
    }
}
