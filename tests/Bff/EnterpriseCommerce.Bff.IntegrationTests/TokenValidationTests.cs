using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>
/// Token response and ID token validation against a programmable provider, for the failures Identity never produces:
/// a wrong issuer, audience or nonce, an access token for another front end, a missing refresh token. The provider
/// also verifies PKCE S256 at its token endpoint.
/// </summary>
public sealed class TokenValidationTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Theory]
    [InlineData(StubScenario.Valid, true)]
    [InlineData(StubScenario.WrongIssuer, false)]
    [InlineData(StubScenario.WrongAudience, false)]
    [InlineData(StubScenario.WrongNonce, false)]
    [InlineData(StubScenario.MissingNonce, false)]
    [InlineData(StubScenario.Expired, false)]
    [InlineData(StubScenario.Unsigned, false)]
    [InlineData(StubScenario.ForeignAccessTokenAudience, false)]
    [InlineData(StubScenario.NoRefreshToken, false)]
    public async Task TheTokenResponse_IsFullyValidated_BeforeASessionExists(StubScenario scenario, bool accepted)
    {
        environment.RequireAvailable();
        await using var provider = await StubProvider.StartAsync(scenario);
        var bff = environment.CreateBff(environment.Storefront, options =>
        {
            options.Authority = StubProvider.Issuer.AbsoluteUri;
            options.AuthorityHandler = provider.Handler;
        });
        using var browser = environment.CreateBrowser(bff).Map(StubProvider.Issuer, provider.Handler);

        var landing = await browser.NavigateAsync(bff.Url("/bff/login?returnUrl=%2Fdone"));

        Assert.True(provider.PkceVerified, "The provider did not receive a valid S256 code verifier.");
        if (accepted)
        {
            Assert.Equal(bff.Url("/done"), landing.Url);
            Assert.NotNull(browser.Cookie(bff.Origin, bff.SessionCookieName));
        }
        else
        {
            Assert.True(landing.Status == HttpStatusCode.BadRequest && landing.Url.AbsolutePath == "/bff/callback", $"{scenario}: {landing.Status} at {landing.Url.AbsolutePath}");
            Assert.Null(browser.Cookie(bff.Origin, bff.SessionCookieName));
            Assert.DoesNotContain("IDX", landing.Body, StringComparison.Ordinal);
        }
    }
}

public enum StubScenario
{
    Valid,
    WrongIssuer,
    WrongAudience,
    WrongNonce,
    MissingNonce,
    Expired,
    Unsigned,
    ForeignAccessTokenAudience,
    NoRefreshToken,
}

/// <summary>A minimal OpenID provider with programmable ID tokens.</summary>
internal sealed class StubProvider : IAsyncDisposable
{
    public static readonly Uri Issuer = new("https://op.example.test/");

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _keyId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, (string? Nonce, string Challenge, string ClientId)> _codes = new();
    private WebApplication? _app;

    private StubProvider(StubScenario scenario) => Scenario = scenario;

    public StubScenario Scenario { get; }

    public bool PkceVerified { get; private set; }

    public HttpMessageHandler Handler => _app!.GetTestServer().CreateHandler();

    public static async Task<StubProvider> StartAsync(StubScenario scenario)
    {
        var provider = new StubProvider(scenario);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Issuer.AbsoluteUri,
            ["authorization_endpoint"] = new Uri(Issuer, "authorize").AbsoluteUri,
            ["token_endpoint"] = new Uri(Issuer, "token").AbsoluteUri,
            ["jwks_uri"] = new Uri(Issuer, "jwks").AbsoluteUri,
            ["end_session_endpoint"] = new Uri(Issuer, "endsession").AbsoluteUri,
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
        }));
        app.MapGet("/jwks", () =>
        {
            var parameters = provider._key.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = provider._keyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } } });
        });
        app.MapGet("/authorize", (HttpContext context) =>
        {
            var query = context.Request.Query;
            if (query["code_challenge_method"] != "S256" || string.IsNullOrEmpty(query["code_challenge"]))
            {
                return Results.BadRequest();
            }

            var code = Guid.NewGuid().ToString("N");
            provider._codes[code] = (query["nonce"], query["code_challenge"]!, query["client_id"]!);
            return Results.Redirect(QueryHelpers.AddQueryString(query["redirect_uri"]!, new Dictionary<string, string?> { ["code"] = code, ["state"] = query["state"] }));
        });
        app.MapPost("/token", async (HttpContext context) =>
        {
            var form = await context.Request.ReadFormAsync();
            if (!provider._codes.TryRemove(form["code"].ToString(), out var grant))
            {
                return Results.BadRequest(new { error = "invalid_grant" });
            }

            var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
            provider.PkceVerified = challenge == grant.Challenge && !string.IsNullOrEmpty(form["client_assertion"]);
            if (!provider.PkceVerified)
            {
                return Results.BadRequest(new { error = "invalid_grant" });
            }

            var response = new Dictionary<string, object>
            {
                ["access_token"] = provider.Token("ec-" + grant.ClientId["bff-".Length..], scenario == StubScenario.ForeignAccessTokenAudience ? "ec-admin" : null, grant.Nonce, accessToken: true),
                ["id_token"] = provider.Token(grant.ClientId, null, grant.Nonce, accessToken: false),
                ["token_type"] = "Bearer",
                ["expires_in"] = 600,
            };
            if (scenario != StubScenario.NoRefreshToken)
            {
                response["refresh_token"] = Guid.NewGuid().ToString("N");
            }

            return Results.Json(response);
        });
        provider._app = app;
        await app.StartAsync();
        return provider;
    }

    private string Token(string audience, string? audienceOverride, string? nonce, bool accessToken)
    {
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["sub"] = "stub-user" };
        if (!accessToken && Scenario != StubScenario.MissingNonce && nonce is not null)
        {
            claims["nonce"] = Scenario == StubScenario.WrongNonce ? "another-nonce" : nonce;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = !accessToken && Scenario == StubScenario.WrongIssuer ? "https://evil.example.test/" : Issuer.AbsoluteUri,
            Audience = audienceOverride ?? (!accessToken && Scenario == StubScenario.WrongAudience ? "bff-other" : audience),
            Claims = claims,
            IssuedAt = !accessToken && Scenario == StubScenario.Expired ? now.AddHours(-2) : now,
            NotBefore = !accessToken && Scenario == StubScenario.Expired ? now.AddHours(-2) : now,
            Expires = !accessToken && Scenario == StubScenario.Expired ? now.AddHours(-1) : now.AddMinutes(10),
            SigningCredentials = !accessToken && Scenario == StubScenario.Unsigned
                ? null
                : new SigningCredentials(new RsaSecurityKey(_key) { KeyId = _keyId }, SecurityAlgorithms.RsaSha256),
            Subject = new ClaimsIdentity(),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _key.Dispose();
    }
}
