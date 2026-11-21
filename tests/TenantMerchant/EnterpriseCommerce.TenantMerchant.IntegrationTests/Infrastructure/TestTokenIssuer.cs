using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

/// <summary>What a test token says. The defaults describe a valid token of an allow-listed service caller.</summary>
public sealed record TokenSpec
{
    public string Subject { get; init; } = TestTokenIssuer.AdminClient;

    public string? ClientId { get; init; } = TestTokenIssuer.AdminClient;

    public string? PrincipalType { get; init; } = "service";

    public string[] Audiences { get; init; } = [TestTokenIssuer.ServiceAudience];

    public string? Scope { get; init; } = TestTokenIssuer.ProvisioningScope;

    public string? Issuer { get; init; }

    public string TokenType { get; init; } = "at+jwt";

    public string Algorithm { get; init; } = SecurityAlgorithms.RsaSha256;

    public TimeSpan ExpiresIn { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Signs with a key that Identity's JWKS does not publish.</summary>
    public bool SignWithUnknownKey { get; init; }

    /// <summary>Extra claims, for example a forged role or tenant claim that must be ignored.</summary>
    public IReadOnlyDictionary<string, object> ExtraClaims { get; init; } = new Dictionary<string, object>();

    /// <summary>A merchant-portal user token (ADR-017: audience <c>ec-merchant</c>, client <c>bff-merchant</c>).</summary>
    public static TokenSpec User(string subject) => new()
    {
        Subject = subject,
        ClientId = "bff-merchant",
        PrincipalType = "user",
        Audiences = [TestTokenIssuer.UserAudience],
        Scope = "openid offline_access",
    };
}

/// <summary>
/// A stand-in for Identity as the token issuer: it signs RS256 <c>at+jwt</c> access tokens with the claim shape of
/// IDENTITY_API_CONTRACT.md section 3, and serves the OpenID Connect discovery document and the JWKS to the service's
/// own JWT bearer handler through its back channel. The service's validation configuration is therefore exercised
/// end to end, including metadata retrieval, without a dependency on the Identity projects.
/// </summary>
public sealed class TestTokenIssuer : IDisposable
{
    public const string Issuer = "https://identity.test/";
    public const string ServiceAudience = "tenant-merchant-api";
    public const string UserAudience = "ec-merchant";
    public const string ProvisioningScope = "svc.tenant-merchant.provisioning";
    public const string AdminClient = "svc-test-admin";

    private readonly RSA _signingKey = RSA.Create(3072);
    private readonly RSA _unknownKey = RSA.Create(3072);
    private readonly string _keyId = Guid.NewGuid().ToString("N");

    /// <summary>Serves discovery and the JWKS; plug into <c>JwtBearerOptions.BackchannelHttpHandler</c>.</summary>
    public HttpMessageHandler CreateBackChannel() => new MetadataHandler(this);

    public string CreateToken(TokenSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var now = DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [JwtRegisteredClaimNames.Sub] = spec.Subject,
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            [JwtRegisteredClaimNames.Aud] = spec.Audiences.Length == 1 ? spec.Audiences[0] : spec.Audiences,
        };
        if (spec.ClientId is not null)
        {
            claims["client_id"] = spec.ClientId;
        }

        if (spec.PrincipalType is not null)
        {
            claims["ec_ptype"] = spec.PrincipalType;
        }

        if (spec.Scope is not null)
        {
            claims["scope"] = spec.Scope;
        }

        foreach (var (type, value) in spec.ExtraClaims)
        {
            claims[type] = value;
        }

        // An expired token was also issued in the past, so that only its expiry is wrong.
        var issuedAt = spec.ExpiresIn < TimeSpan.Zero ? now + spec.ExpiresIn - TimeSpan.FromMinutes(5) : now - TimeSpan.FromSeconds(1);
        var key = new RsaSecurityKey(spec.SignWithUnknownKey ? _unknownKey : _signingKey) { KeyId = _keyId };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = spec.Issuer ?? Issuer,
            Claims = claims,
            TokenType = spec.TokenType,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = (now + spec.ExpiresIn).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, spec.Algorithm),
        };

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        _unknownKey.Dispose();
    }

    private string Discovery() => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["issuer"] = Issuer,
        ["jwks_uri"] = $"{Issuer}.well-known/jwks",
        ["id_token_signing_alg_values_supported"] = new[] { SecurityAlgorithms.RsaSha256 },
    });

    private string Jwks()
    {
        var parameters = _signingKey.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = SecurityAlgorithms.RsaSha256,
                    kid = _keyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
    }

    private sealed class MetadataHandler(TestTokenIssuer issuer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsoluteUri switch
            {
                $"{Issuer}.well-known/openid-configuration" => issuer.Discovery(),
                $"{Issuer}.well-known/jwks" => issuer.Jwks(),
                _ => null,
            };

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
