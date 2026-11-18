using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>A registered test client and the private key it authenticates with (<c>private_key_jwt</c>).</summary>
public sealed class TestClient(string clientId, string? audience) : IDisposable
{
    private readonly RSA _key = RSA.Create(3072);
    private readonly string _keyId = Guid.NewGuid().ToString("N");

    public string ClientId { get; } = clientId;

    public string? Audience { get; } = audience;

    public string RedirectUri => $"https://{ClientId}.example.test/bff/callback";

    public string JsonWebKeySet
    {
        get
        {
            var parameters = _key.ExportParameters(includePrivateParameters: false);
            return JsonSerializer.Serialize(new
            {
                keys = new[]
                {
                    new { kty = "RSA", use = "sig", alg = "RS256", kid = _keyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) },
                },
            });
        }
    }

    /// <summary>Creates a fresh client assertion (RFC 7523) for the token endpoint.</summary>
    public string CreateAssertion(Uri issuer)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            // RFC 7523bis explicit typing; OpenIddict 7.9 rejects untyped client assertions (ID2089).
            TokenType = "client-authentication+jwt",
            Issuer = ClientId,
            Audience = issuer.AbsoluteUri,
            Subject = new ClaimsIdentity([new Claim("sub", ClientId), new Claim("jti", Guid.NewGuid().ToString())]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = _keyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Result of a token request.</summary>
public sealed record TokenResponse(HttpStatusCode Status, JsonElement Body)
{
    public string? AccessToken => Body.TryGetProperty("access_token", out var value) ? value.GetString() : null;

    public string? RefreshToken => Body.TryGetProperty("refresh_token", out var value) ? value.GetString() : null;

    public string? Error => Body.TryGetProperty("error", out var value) ? value.GetString() : null;
}

/// <summary>
/// The real Identity host on a throwaway SQL Server database, with three BFF clients and one service client.
/// Drives the browser login and the authorization code flow with PKCE like a BFF would.
/// </summary>
public sealed partial class IdentityServerFixture : IAsyncLifetime
{
    public const string Password = "Correct-Horse-Battery-9";
    public static readonly Uri Issuer = new("https://localhost/");

    private readonly SqlServerTestDatabase _database = new();
    private WebApplicationFactory<Program>? _factory;

    public TestClient Storefront { get; } = new("bff-storefront", "ec-storefront");

    public TestClient Merchant { get; } = new("bff-merchant", "ec-merchant");

    public TestClient Admin { get; } = new("bff-admin", "ec-admin");

    public TestClient Service { get; } = new("svc-test-orders", audience: null);

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("SQL Server is not configured.");

    public IServiceProvider Services => Factory.Services;

    public void RequireAvailable() => _database.RequireAvailable();

    public async ValueTask InitializeAsync()
    {
        await _database.InitializeAsync();
        if (string.IsNullOrEmpty(_database.ConnectionString))
        {
            return;
        }

        _factory = new ServerFactory(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:IdentityDatabase", _database.ConnectionString);
            builder.UseSetting("Identity:Issuer", Issuer.AbsoluteUri);
            builder.UseSetting("Identity:AdminConsoleClientId", Admin.ClientId);
            builder.UseSetting("Identity:RateLimits:CredentialRequestsPerMinute", "100000");
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");

            var index = 0;
            foreach (var client in new[] { Storefront, Merchant, Admin })
            {
                var prefix = $"Identity:Clients:{index++}:";
                builder.UseSetting(prefix + "ClientId", client.ClientId);
                builder.UseSetting(prefix + "Kind", "Browser");
                builder.UseSetting(prefix + "Audience", client.Audience);
                builder.UseSetting(prefix + "RedirectUris:0", client.RedirectUri);
                builder.UseSetting(prefix + "JsonWebKeySet", client.JsonWebKeySet);
            }

            var service = $"Identity:Clients:{index}:";
            builder.UseSetting(service + "ClientId", Service.ClientId);
            builder.UseSetting(service + "Kind", "Service");
            builder.UseSetting(service + "JsonWebKeySet", Service.JsonWebKeySet);
            builder.UseSetting(service + "ServiceScopes:0:Scope", "svc.inventory.reservations");
            builder.UseSetting(service + "ServiceScopes:0:Audience", "inventory-api");
        });

        // Starts the host, which registers the clients.
        _ = _factory.Server;
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _database.DisposeAsync();
        Storefront.Dispose();
        Merchant.Dispose();
        Admin.Dispose();
        Service.Dispose();
    }

    /// <summary>A browser: follows no redirects automatically and keeps cookies.</summary>
    public HttpClient CreateBrowser() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = Issuer,
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    /// <summary>A back-channel client (BFF to Identity): no cookies.</summary>
    public HttpClient CreateBackChannel() => Factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = Issuer,
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    public async Task<string> CreateUserAsync(bool emailConfirmed = true, bool withSecurityVersion = true)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"user-{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = emailConfirmed, CreatedAt = DateTimeOffset.UtcNow };

        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Code)));

        if (withSecurityVersion)
        {
            var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
            context.SecurityVersions.Add(new SecurityVersionRecord { UserId = user.Id, Version = SecurityVersionPolicy.InitialVersion });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return email;
    }

    public async Task<string> GetUserIdAsync(string email)
    {
        await using var scope = Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
        return user!.Id;
    }

    /// <summary>Submits the Identity login form. Returns the response (a redirect on success).</summary>
    public async Task<HttpResponseMessage> SubmitLoginAsync(HttpClient browser, string email, string password, string? returnUrl = null, bool sameOrigin = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var loginUrl = "/account/login" + (returnUrl is null ? string.Empty : "?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var page = await browser.GetStringAsync(loginUrl, ct);
        var antiforgery = AntiforgeryField().Match(page).Groups[1].Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Input.Email"] = email,
                ["Input.Password"] = password,
                ["ReturnUrl"] = returnUrl ?? string.Empty,
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(antiforgery),
            }),
        };

        if (sameOrigin)
        {
            request.Headers.Add("Origin", "https://localhost");
        }

        return await browser.SendAsync(request, ct);
    }

    /// <summary>Signs in through the Identity login page and returns the authorization code for the client.</summary>
    public async Task<(string Code, string Verifier)> AuthorizeAsync(HttpClient browser, TestClient client, string email)
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var authorizeUrl = BuildAuthorizeUrl(client, CodeChallenge(verifier), "S256");

        using var first = await browser.GetAsync(authorizeUrl, ct);
        var location = first.Headers.Location?.OriginalString ?? throw new InvalidOperationException($"No redirect from the authorization endpoint ({first.StatusCode}).");

        if (location.Contains("/account/login", StringComparison.Ordinal))
        {
            using var login = await SubmitLoginAsync(browser, email, Password, authorizeUrl);
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

            using var second = await browser.GetAsync(authorizeUrl, ct);
            location = second.Headers.Location?.OriginalString ?? throw new InvalidOperationException($"No redirect after login ({second.StatusCode}).");
        }

        Assert.StartsWith(client.RedirectUri, location, StringComparison.Ordinal);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(location).Query);
        return (query["code"].ToString(), verifier);
    }

    public string BuildAuthorizeUrl(TestClient client, string? challenge, string? method) =>
        "/connect/authorize?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(client.ClientId)
        + "&redirect_uri=" + Uri.EscapeDataString(client.RedirectUri)
        + "&scope=" + Uri.EscapeDataString("openid offline_access")
        + "&state=" + Guid.NewGuid().ToString("N")
        + "&nonce=" + Guid.NewGuid().ToString("N")
        + (challenge is null ? string.Empty : "&code_challenge=" + challenge)
        + (method is null ? string.Empty : "&code_challenge_method=" + method);

    /// <summary>Signs in and exchanges the code, returning the first token response of a new session.</summary>
    public async Task<TokenResponse> SignInAsync(TestClient client, string email, HttpClient? browser = null)
    {
        using var ownBrowser = browser is null ? CreateBrowser() : null;
        var (code, verifier) = await AuthorizeAsync(browser ?? ownBrowser!, client, email);
        return await ExchangeCodeAsync(client, code, verifier);
    }

    public Task<TokenResponse> ExchangeCodeAsync(TestClient client, string code, string verifier) =>
        RequestTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = client.RedirectUri,
        });

    public Task<TokenResponse> RefreshAsync(TestClient client, string refreshToken) =>
        RequestTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        });

    public async Task<TokenResponse> RequestTokenAsync(TestClient client, Dictionary<string, string> parameters)
    {
        parameters["client_id"] = client.ClientId;
        parameters["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
        parameters["client_assertion"] = client.CreateAssertion(Issuer);

        using var backChannel = CreateBackChannel();
        using var response = await backChannel.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(parameters), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = string.IsNullOrWhiteSpace(body) ? "{}" : body;
        return new TokenResponse(response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
    }

    public static string CodeChallenge(string verifier) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private sealed class ServerFactory(Action<IWebHostBuilder> configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();
}
