using System.Net;
using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Protocol;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Bff.UnitTests;

public sealed class ClientAssertionFactoryTests
{
    [Fact]
    public async Task Assertion_HasTheShapeIdentityRequires()
    {
        using var rsa = RSA.Create(3072);
        var settings = BffSettingsValidationTests.Valid();
        settings.ClientKey = new BffClientKeySettings { PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(), KeyId = "kid-1" };
        using var factory = new ClientAssertionFactory(Options.Create(settings), TimeProvider.System);

        var first = factory.Create();
        var second = factory.Create();

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(first, new TokenValidationParameters
        {
            ValidIssuer = settings.ClientId,
            ValidAudience = settings.Authority,
            IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "kid-1" },
            ValidTypes = [ClientAssertionFactory.TokenType],
        });
        Assert.True(result.IsValid, result.Exception?.Message);

        var token = new JsonWebToken(first);
        Assert.Equal("client-authentication+jwt", token.Typ);
        Assert.Equal("RS256", token.Alg);
        Assert.Equal("kid-1", token.Kid);
        Assert.Equal(settings.ClientId, token.Subject);
        Assert.Equal(TimeSpan.FromMinutes(2), token.ValidTo - token.IssuedAt);
        Assert.NotEqual(token.Id, new JsonWebToken(second).Id);
    }

    [Fact]
    public void EcKeys_AreSupported()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var settings = BffSettingsValidationTests.Valid();
        settings.ClientKey = new BffClientKeySettings { PrivateKeyPem = ec.ExportPkcs8PrivateKeyPem(), KeyId = "ec-1" };
        using var factory = new ClientAssertionFactory(Options.Create(settings), TimeProvider.System);

        Assert.Equal("ES256", new JsonWebToken(factory.Create()).Alg);
    }

    [Fact]
    public void WeakRsaKeys_AreRefused()
    {
        using var rsa = RSA.Create(1024);
        Assert.Throws<InvalidOperationException>(() => ClientAssertionFactory.LoadKey(rsa.ExportPkcs8PrivateKeyPem(), "weak"));
    }
}

public sealed class RefreshClassificationTests
{
    private const string TokenEndpoint = "https://id.example.test/connect/token";

    [Fact]
    public async Task Success_ReturnsTheRotatedTokenSet()
    {
        var access = AccessToken("ec-storefront");
        var result = await RefreshReturning(_ => Json(HttpStatusCode.OK, $$"""{"access_token":"{{access}}","refresh_token":"rt-2","id_token":"id-2","expires_in":600}"""));

        Assert.Equal(RefreshOutcome.Success, result.Outcome);
        Assert.Equal("rt-2", result.Tokens!.RefreshToken);
        Assert.Equal(TimeSpan.FromMinutes(10), result.Tokens.AccessTokenExpiresAt - result.Tokens.AccessTokenIssuedAt);
    }

    [Fact]
    public async Task ForeignAudience_IsNotAccepted()
    {
        var result = await RefreshReturning(_ => Json(HttpStatusCode.OK, $$"""{"access_token":"{{AccessToken("ec-admin")}}","refresh_token":"rt-2","expires_in":600}"""));

        Assert.Equal(RefreshOutcome.ClientRejected, result.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""", "InvalidGrant")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_client"}""", "ClientRejected")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""", "ClientRejected")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"unauthorized_client"}""", "ClientRejected")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":"temporarily_unavailable"}""", "Unknown")]
    [InlineData(HttpStatusCode.InternalServerError, "", "Unknown")]
    [InlineData(HttpStatusCode.BadGateway, "", "Unknown")]
    [InlineData(HttpStatusCode.OK, """{"token_type":"Bearer"}""", "Unknown")]
    [InlineData(HttpStatusCode.OK, "not json", "Unknown")]
    public async Task Responses_AreClassified(HttpStatusCode status, string body, string expected)
    {
        var result = await RefreshReturning(_ => Json(status, body));

        Assert.Equal(expected, result.Outcome.ToString());
    }

    [Fact]
    public async Task ConnectionFailure_BeforeSending_IsNotSent()
    {
        var result = await RefreshReturning(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));

        Assert.Equal(RefreshOutcome.NotSent, result.Outcome);
    }

    [Fact]
    public async Task ResetAfterSending_IsUnknown()
    {
        var result = await RefreshReturning(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "reset"));

        Assert.Equal(RefreshOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task Timeout_IsUnknown()
    {
        var result = await RefreshWith(async cancellationToken =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal(RefreshOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task TheRefreshRequest_CarriesAClientAssertionAndNoSecret()
    {
        string? body = null;
        await RefreshWith(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        });

        Assert.Contains("grant_type=refresh_token", body, StringComparison.Ordinal);
        Assert.Contains("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer", body, StringComparison.Ordinal);
        Assert.Contains("client_assertion=ey", body, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", body, StringComparison.Ordinal);
    }

    private static string AccessToken(string audience) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Audience = audience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256),
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<RefreshResult> RefreshReturning(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        RefreshWith(request => Task.FromResult(respond(request)));

    private static Task<RefreshResult> RefreshWith(Func<CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan timeout) =>
        RefreshWith((_, cancellationToken) => respond(cancellationToken), timeout);

    private static Task<RefreshResult> RefreshWith(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) =>
        RefreshWith((request, _) => respond(request), TimeSpan.FromSeconds(5));

    private static async Task<RefreshResult> RefreshWith(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan timeout)
    {
        using var rsa = RSA.Create(2048);
        var settings = BffSettingsValidationTests.Valid();
        settings.ClientKey = new BffClientKeySettings { PrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(), KeyId = "k" };
        settings.Refresh.RequestTimeout = timeout;
        settings.Refresh.LeaseDuration = timeout + TimeSpan.FromSeconds(1);

        var oidc = new OpenIdConnectOptions
        {
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration { TokenEndpoint = TokenEndpoint }),
        };
        using var assertions = new ClientAssertionFactory(Options.Create(settings), TimeProvider.System);
        var client = new IdentityProtocolClient(
            new StubHttpClientFactory(new StubHandler(respond)),
            new StaticOptionsMonitor(oidc),
            assertions,
            Options.Create(settings),
            TimeProvider.System);

        return await client.RefreshAsync("rt-1", TestContext.Current.CancellationToken);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticOptionsMonitor(OpenIdConnectOptions options) : IOptionsMonitor<OpenIdConnectOptions>
    {
        public OpenIdConnectOptions CurrentValue => options;

        public OpenIdConnectOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => null;
    }
}
