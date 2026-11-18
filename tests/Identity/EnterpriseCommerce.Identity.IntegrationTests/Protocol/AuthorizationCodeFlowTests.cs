using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>
/// Login on the Identity page, authorization code + PKCE, and token issuance, validated by a resource server that
/// uses only the standard ASP.NET Core JWT bearer handler (ADR-017 sections 1 to 4, PoC-12).
/// </summary>
public sealed class AuthorizationCodeFlowTests(IdentityServerFixture identity) : IClassFixture<IdentityServerFixture>
{
    [Fact]
    public async Task CodeFlow_IssuesMinimalRs256AccessToken_AcceptedByStandardJwtBearerForItsAudienceOnly()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();

        var tokens = await identity.SignInAsync(identity.Storefront, email);

        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        Assert.False(string.IsNullOrEmpty(tokens.RefreshToken));

        var jwt = new JsonWebToken(tokens.AccessToken);
        Assert.Equal("RS256", jwt.Alg);
        Assert.Equal("at+jwt", jwt.Typ);
        Assert.Equal(["ec-storefront"], jwt.Audiences);
        Assert.Equal(await identity.GetUserIdAsync(email), jwt.Subject);
        Assert.Equal("user", jwt.GetClaim("ec_ptype").Value);
        Assert.Equal("bff-storefront", jwt.GetClaim("client_id").Value);
        foreach (var forbidden in new[] { "roles", "role", "tenant_context", "email", "name", "ec_sv", "ec_sdl" })
        {
            Assert.False(jwt.TryGetClaim(forbidden, out _), $"The access token must not carry '{forbidden}'.");
        }

        await using var storefrontApi = await StartResourceServerAsync("ec-storefront");
        await using var merchantApi = await StartResourceServerAsync("ec-merchant");

        Assert.Equal(HttpStatusCode.OK, await CallAsync(storefrontApi, tokens.AccessToken!));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync(merchantApi, tokens.AccessToken!));
    }

    [Fact]
    public async Task Jwks_PublishesOnlyRsaKeysOfAtLeast3072Bits()
    {
        identity.RequireAvailable();
        using var client = identity.CreateBackChannel();

        using var jwks = JsonDocument.Parse(await client.GetStringAsync(new Uri("/.well-known/jwks", UriKind.Relative), TestContext.Current.CancellationToken));

        var keys = jwks.RootElement.GetProperty("keys").EnumerateArray().ToList();
        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.Equal("RSA", key.GetProperty("kty").GetString());
            Assert.True(Base64UrlEncoder.DecodeBytes(key.GetProperty("n").GetString()).Length * 8 >= 3072);
            Assert.False(key.TryGetProperty("d", out _), "Private key material must never be published.");
        });
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("plain", "plain")]
    public async Task AuthorizationRequest_WithoutS256Pkce_IsRejected(string? challengeKind, string? method)
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();
        using var login = await identity.SubmitLoginAsync(browser, email, IdentityServerFixture.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var challenge = challengeKind is null ? null : "a-plain-verifier-that-is-at-least-43-characters-long-xx";
        using var response = await browser.GetAsync(
            identity.BuildAuthorizeUrl(identity.Storefront, challenge, method),
            TestContext.Current.CancellationToken);

        // Rejected either directly (400) or by an error redirect; in no case is an authorization code issued.
        var location = response.Headers.Location?.OriginalString ?? string.Empty;
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest || location.Contains("error=invalid_request", StringComparison.Ordinal),
            $"Expected a rejection, got {response.StatusCode} {location}");
        Assert.DoesNotContain("code=", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodeExchange_WithWrongVerifier_IsRejected()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();
        var (code, _) = await identity.AuthorizeAsync(browser, identity.Storefront, email);

        var response = await identity.ExchangeCodeAsync(identity.Storefront, code, "wrong-verifier-wrong-verifier-wrong-verifier-123");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalid_grant", response.Error);
    }

    [Fact]
    public async Task CodeExchange_ByAnotherClient_IsRejected()
    {
        identity.RequireAvailable();
        var email = await identity.CreateUserAsync();
        using var browser = identity.CreateBrowser();
        var (code, verifier) = await identity.AuthorizeAsync(browser, identity.Storefront, email);

        var response = await identity.RequestTokenAsync(identity.Merchant, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = identity.Storefront.RedirectUri,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalid_grant", response.Error);
    }

    [Fact]
    public async Task ServiceClient_GetsServiceAudienceToken_AndBrowserClientCannotUseClientCredentials()
    {
        identity.RequireAvailable();

        var service = await identity.RequestTokenAsync(identity.Service, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = "svc.inventory.reservations",
        });
        var browserClient = await identity.RequestTokenAsync(identity.Storefront, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
        });

        Assert.Equal(HttpStatusCode.OK, service.Status);
        var jwt = new JsonWebToken(service.AccessToken);
        Assert.Equal(["inventory-api"], jwt.Audiences);
        Assert.Equal("service", jwt.GetClaim("ec_ptype").Value);
        Assert.Equal("svc-test-orders", jwt.Subject);
        Assert.Equal(HttpStatusCode.BadRequest, browserClient.Status);
    }

    private async Task<WebApplication> StartResourceServerAsync(string audience)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            // Configured as ADR-017 section 4 requires for every service.
            options.Authority = IdentityServerFixture.Issuer.AbsoluteUri;
            options.Audience = audience;
            options.MapInboundClaims = false;
            options.BackchannelHttpHandler = identity.Factory.Server.CreateHandler();
            options.TokenValidationParameters.ValidIssuer = IdentityServerFixture.Issuer.AbsoluteUri;
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
            options.TokenValidationParameters.ValidTypes = ["at+jwt"];
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/resource", () => Results.Ok()).RequireAuthorization();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<HttpStatusCode> CallAsync(WebApplication api, string accessToken)
    {
        using var client = api.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/resource");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
