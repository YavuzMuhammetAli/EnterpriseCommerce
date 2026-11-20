using System.Net;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Access token lifetimes stay separated by client kind (finding I38, PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.4):
/// service clients (<c>svc-</c>) get 5-minute tokens, browser (BFF) clients keep their 10-minute tokens, and the refresh
/// token lifetimes are unchanged. The fixture configures no lifetime, so every value below is a default.
/// </summary>
public sealed class TokenLifetimeTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    private static readonly TimeSpan ServiceTokenLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UserTokenLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromHours(2);

    [Fact]
    public async Task AServiceAccessToken_LivesAtMostFiveMinutes()
    {
        identity.RequireAvailable();

        var response = await ServiceTokenAsync(identity.Service, "svc.inventory.reservations");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var jwt = new JsonWebToken(response.AccessToken);
        Assert.Equal(ServiceTokenLifetime, jwt.ValidTo - jwt.IssuedAt);
        Assert.Equal("service", jwt.GetClaim("ec_ptype").Value);
        var expiresIn = response.Body.GetProperty("expires_in").GetInt64();
        Assert.InRange(expiresIn, 1, (long)ServiceTokenLifetime.TotalSeconds);
        Assert.False(response.Body.TryGetProperty("refresh_token", out _));
    }

    [Fact]
    public async Task BrowserClientUserAccessTokens_KeepTheirTenMinuteLifetime()
    {
        identity.RequireAvailable();

        foreach (var client in new[] { identity.Storefront, identity.Admin })
        {
            var session = await identity.SignInAsync(client, await identity.CreateUserAsync());

            Assert.Equal(HttpStatusCode.OK, session.Status);
            var jwt = new JsonWebToken(session.AccessToken);
            Assert.Equal(UserTokenLifetime, jwt.ValidTo - jwt.IssuedAt);
            Assert.Equal("user", jwt.GetClaim("ec_ptype").Value);
            Assert.Equal([client.Audience!], jwt.Audiences);
        }
    }

    [Fact]
    public async Task RefreshTokens_KeepTheirLifetimeAndRotation()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        var userId = await identity.GetUserIdAsync(email);

        var session = await identity.SignInAsync(identity.Storefront, email);
        var rotated = await identity.RefreshAsync(identity.Storefront, session.RefreshToken!);

        Assert.Equal(HttpStatusCode.OK, rotated.Status);
        Assert.NotEqual(session.RefreshToken, rotated.RefreshToken);
        var successor = new JsonWebToken(rotated.AccessToken);
        Assert.Equal(UserTokenLifetime, successor.ValidTo - successor.IssuedAt);

        // Both refresh tokens (code exchange and rotation) carry the client's sliding lifetime, capped by the session.
        var lifetimes = await RefreshTokenLifetimesAsync(userId);
        Assert.Equal(2, lifetimes.Count);
        Assert.All(lifetimes, lifetime => Assert.InRange(lifetime, RefreshTokenLifetime - TimeSpan.FromSeconds(1), RefreshTokenLifetime + TimeSpan.FromSeconds(1)));

        // No reuse leeway: the redeemed token is rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await identity.RefreshAsync(identity.Storefront, session.RefreshToken!)).Status);
    }

    [Fact]
    public async Task UnknownOrUnauthorizedClients_GetNoServiceToken()
    {
        identity.RequireAvailable();
        using var unknown = new TestClient("svc-test-unknown", audience: null);
        using var impostor = new TestClient(identity.Service.ClientId, audience: null);

        // An unregistered client, and a registered client ID with an assertion signed by a key it does not own.
        AssertRejected(await ServiceTokenAsync(unknown, "svc.inventory.reservations"), "invalid_client");
        AssertRejected(await ServiceTokenAsync(impostor, "svc.inventory.reservations"), "invalid_client");

        // A browser client may not use client credentials.
        AssertRejected(await ServiceTokenAsync(identity.Storefront, "svc.inventory.reservations"), "unauthorized_client");

        // A service client gets neither a scope it is not registered for (OpenIddict's scope permission check answers
        // invalid_request), nor a token without a service scope (Identity's own check).
        AssertRejected(await ServiceTokenAsync(identity.Service, IdentityServerFixture.AccountLifecycleScope), "invalid_request");
        AssertRejected(await identity.RequestTokenAsync(identity.Service, new Dictionary<string, string> { ["grant_type"] = "client_credentials" }), "invalid_scope");
    }

    [Fact]
    public async Task ResourceServers_StillValidateServiceAndUserTokensForTheirAudienceOnly()
    {
        identity.RequireAvailable();
        var service = await ServiceTokenAsync(identity.Service, "svc.inventory.reservations");
        var user = await identity.SignInAsync(identity.Storefront, await identity.CreateUserAsync());

        await using var inventory = await ResourceServer.StartAsync(identity.Factory.Server.CreateHandler(), "inventory-api");
        await using var storefrontApi = await ResourceServer.StartAsync(identity.Factory.Server.CreateHandler(), "ec-storefront");

        Assert.Equal(HttpStatusCode.OK, await ResourceServer.CallAsync(inventory, service.AccessToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, await ResourceServer.CallAsync(inventory, user.AccessToken!));
        Assert.Equal(HttpStatusCode.OK, await ResourceServer.CallAsync(storefrontApi, user.AccessToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, await ResourceServer.CallAsync(storefrontApi, service.AccessToken!));
    }

    private Task<TokenResponse> ServiceTokenAsync(TestClient client, string scope) =>
        identity.RequestTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = scope,
        });

    private static void AssertRejected(TokenResponse response, string error)
    {
        Assert.True(response.Status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, $"{response.Status}");
        Assert.Equal(error, response.Error);
        Assert.Null(response.AccessToken);
    }

    private async Task<List<TimeSpan>> RefreshTokenLifetimesAsync(string userId)
    {
        await using var scope = identity.Services.CreateAsyncScope();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var lifetimes = new List<TimeSpan>();
        await foreach (var token in tokens.FindBySubjectAsync(userId, TestContext.Current.CancellationToken))
        {
            if (await tokens.GetTypeAsync(token, TestContext.Current.CancellationToken) is TokenTypeHints.RefreshToken or TokenTypeIdentifiers.RefreshToken)
            {
                var created = await tokens.GetCreationDateAsync(token, TestContext.Current.CancellationToken);
                var expires = await tokens.GetExpirationDateAsync(token, TestContext.Current.CancellationToken);
                lifetimes.Add(expires!.Value - created!.Value);
            }
        }

        return lifetimes;
    }
}
