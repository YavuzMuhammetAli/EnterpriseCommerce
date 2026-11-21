using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>
/// The Identity verification client (ADR-021): it maps exactly the documented answers, fails closed on everything
/// else, caches its service token, renews it once after a <c>401</c>, and refuses an incomplete configuration.
/// </summary>
public sealed class IdentityAccountVerifierTests : IDisposable
{
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"ec-tm-unit-{Guid.NewGuid():N}.pem");
    private readonly ScriptedIdentity _identity = new();

    public IdentityAccountVerifierTests()
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(_keyPath, rsa.ExportPkcs8PrivateKeyPem());
    }

    [Theory]
    [InlineData("Match", IdentityEmailCheck.Match)]
    [InlineData("Mismatch", IdentityEmailCheck.Mismatch)]
    [InlineData("Unverified", IdentityEmailCheck.Unverified)]
    [InlineData("Inactive", IdentityEmailCheck.Inactive)]
    public async Task TheDocumentedAnswers_AreMapped(string answer, IdentityEmailCheck expected)
    {
        _identity.Verification = () => Json(HttpStatusCode.OK, new { accountId = "user-1", result = answer });
        using var verifier = CreateVerifier();

        Assert.Equal(expected, await verifier.Verifier.CheckEmailAsync("user-1", "a@example.test", TestContext.Current.CancellationToken));
        Assert.Equal("/internal/v1/accounts/user-1/email-verification", _identity.LastVerificationPath);
        Assert.Equal("""{"email":"a@example.test"}""", _identity.LastVerificationBody);
    }

    [Fact]
    public async Task AnUnknownAccount_IsAVerdict_NotAFailure()
    {
        _identity.Verification = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        using var verifier = CreateVerifier();

        Assert.Equal(IdentityEmailCheck.UnknownAccount, await verifier.Verifier.CheckEmailAsync("ghost", "a@example.test", TestContext.Current.CancellationToken));
    }

    public static TheoryData<string> Failures() => ["500", "503", "403", "400", "unknown-answer", "no-answer", "not-json", "unreachable", "timeout"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task AnythingElse_FailsClosed(string failure)
    {
        _identity.Verification = failure switch
        {
            "unknown-answer" => () => Json(HttpStatusCode.OK, new { result = "Probably" }),
            "no-answer" => () => Json(HttpStatusCode.OK, new { accountId = "user-1" }),
            "not-json" => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") },
            "unreachable" => () => throw new HttpRequestException("connection refused", null, HttpStatusCode.ServiceUnavailable),
            "timeout" => () => throw new TaskCanceledException("timed out"),
            _ => () => new HttpResponseMessage((HttpStatusCode)int.Parse(failure, System.Globalization.CultureInfo.InvariantCulture)),
        };
        using var verifier = CreateVerifier();

        await Assert.ThrowsAsync<IdentityUnavailableException>(() =>
            verifier.Verifier.CheckEmailAsync("user-1", "a@example.test", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheServiceToken_IsCached_AndRenewedOnceAfterA401()
    {
        var unauthorized = 1;
        _identity.Verification = () => unauthorized-- > 0
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : Json(HttpStatusCode.OK, new { result = "Match" });
        using var verifier = CreateVerifier();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(IdentityEmailCheck.Match, await verifier.Verifier.CheckEmailAsync("user-1", "a@example.test", ct));
        Assert.Equal(2, _identity.TokenRequests);
        Assert.Equal(IdentityEmailCheck.Match, await verifier.Verifier.CheckEmailAsync("user-1", "a@example.test", ct));
        Assert.Equal(2, _identity.TokenRequests);
        Assert.Equal(_identity.IssuedTokens[^1], _identity.LastBearer);

        // A second 401 in a row is not retried forever: it fails closed.
        _identity.Verification = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<IdentityUnavailableException>(() => verifier.Verifier.CheckEmailAsync("user-1", "a@example.test", ct));
        Assert.Equal(3, _identity.TokenRequests);
    }

    [Fact]
    public async Task AFailedTokenRequest_FailsClosed()
    {
        _identity.Token = () => new HttpResponseMessage(HttpStatusCode.BadRequest);
        using var verifier = CreateVerifier();

        await Assert.ThrowsAsync<IdentityUnavailableException>(() =>
            verifier.Verifier.CheckEmailAsync("user-1", "a@example.test", TestContext.Current.CancellationToken));
        Assert.Equal(0, _identity.VerificationRequests);
    }

    [Fact]
    public async Task WithoutConfiguration_NothingIsSent_AndItFailsClosed()
    {
        using var verifier = CreateVerifier(new IdentityVerificationOptions());

        await Assert.ThrowsAsync<IdentityUnavailableException>(() =>
            verifier.Verifier.CheckEmailAsync("user-1", "a@example.test", TestContext.Current.CancellationToken));
        Assert.Equal(0, _identity.TokenRequests + _identity.VerificationRequests);
    }

    [Fact]
    public void Options_AreRequiredOutsideDevelopment_AndNeverPartial()
    {
        Assert.Empty(new IdentityVerificationOptions().Validate(isDevelopment: true));
        Assert.NotEmpty(new IdentityVerificationOptions().Validate(isDevelopment: false));

        var complete = Options();
        Assert.Empty(complete.Validate(isDevelopment: false));
        Assert.Equal(new Uri("https://identity.test/connect/token"), complete.EffectiveTokenEndpoint);

        Assert.NotEmpty(Options(o => o.PrivateKeyPath = null).Validate(isDevelopment: true));
        Assert.NotEmpty(Options(o => o.ClientId = "bff-merchant").Validate(isDevelopment: true));
        Assert.NotEmpty(Options(o => o.ClientId = "svc-").Validate(isDevelopment: true));
        Assert.NotEmpty(Options(o => o.Issuer = null).Validate(isDevelopment: true));
        Assert.NotEmpty(Options(o => o.Timeout = TimeSpan.Zero).Validate(isDevelopment: true));
        Assert.NotEmpty(Options(o => o.BaseAddress = new Uri("http://identity.test/")).Validate(isDevelopment: false));
        Assert.Empty(Options(o => o.BaseAddress = new Uri("http://identity.test/")).Validate(isDevelopment: true));
    }

    public void Dispose()
    {
        _identity.Dispose();
        File.Delete(_keyPath);
    }

    private IdentityVerificationOptions Options(Action<IdentityVerificationOptions>? change = null)
    {
        var options = new IdentityVerificationOptions
        {
            BaseAddress = new Uri("https://identity.test/"),
            Issuer = new Uri("https://identity.test/"),
            PrivateKeyPath = _keyPath,
        };
        change?.Invoke(options);
        return options;
    }

    private VerifierUnderTest CreateVerifier(IdentityVerificationOptions? options = null)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options ?? Options());
        var tokens = new IdentityServiceTokenProvider(new SingleClientFactory(_identity), wrapped, TimeProvider.System);
        var http = new HttpClient(_identity, disposeHandler: false);
        return new VerifierUnderTest(new IdentityAccountVerifier(http, tokens, wrapped, NullLogger<IdentityAccountVerifier>.Instance), tokens, http);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status) { Content = JsonContent.Create(body) };

    private sealed record VerifierUnderTest(IdentityAccountVerifier Verifier, IdentityServiceTokenProvider Tokens, HttpClient Http) : IDisposable
    {
        public void Dispose()
        {
            Tokens.Dispose();
            Http.Dispose();
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ScriptedIdentity : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Token { get; set; } = () => throw new InvalidOperationException("Set by the constructor.");

        public Func<HttpResponseMessage> Verification { get; set; } = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        public List<string> IssuedTokens { get; } = [];

        public int TokenRequests { get; private set; }

        public int VerificationRequests { get; private set; }

        public string? LastVerificationPath { get; private set; }

        public string? LastVerificationBody { get; private set; }

        public string? LastBearer { get; private set; }

        public ScriptedIdentity() => Token = () =>
        {
            var token = "token-" + IssuedTokens.Count;
            IssuedTokens.Add(token);
            return Json(HttpStatusCode.OK, new { access_token = token, expires_in = 300 });
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/connect/token")
            {
                TokenRequests++;
                return Token();
            }

            VerificationRequests++;
            LastVerificationPath = request.RequestUri.AbsolutePath;
            LastVerificationBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastBearer = request.Headers.Authorization?.Parameter;
            return Verification();
        }
    }
}
