using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Catalog.Infrastructure.Membership;

/// <summary>
/// Obtains and caches a client-credentials access token for Tenant &amp; Merchant's internal API, authenticating at
/// Identity with a client assertion (<c>private_key_jwt</c>, typed <c>client-authentication+jwt</c>), as Tenant &amp;
/// Merchant does towards Identity. The token (not any verdict) is renewed a minute before it expires and dropped after a
/// <c>401</c>. It is never logged.
/// </summary>
internal sealed class CatalogServiceTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<MembershipVerificationOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    public const string HttpClientName = "catalog-service-token";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private RsaSecurityKey? _key;
    private (string Value, DateTimeOffset RenewAt)? _cached;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } current && current.RenewAt > timeProvider.GetUtcNow())
        {
            return current.Value;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } refreshed && refreshed.RenewAt > timeProvider.GetUtcNow())
            {
                return refreshed.Value;
            }

            var settings = options.Value;
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = MembershipVerificationOptions.Scope,
                ["client_id"] = settings.ClientId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = CreateAssertion(settings),
            });
            using var response = await client.PostAsync(settings.TokenEndpoint, form, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var value = json.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("The token response has no access token.");
            var lifetime = json.RootElement.TryGetProperty("expires_in", out var expiresIn) ? expiresIn.GetInt32() : 300;

            _cached = (value, timeProvider.GetUtcNow().AddSeconds(Math.Max(lifetime - 60, 0)));
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate() => _cached = null;

    public void Dispose()
    {
        _lock.Dispose();
        _key?.Rsa?.Dispose();
    }

    private string CreateAssertion(MembershipVerificationOptions settings)
    {
        if (_key is null)
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(settings.PrivateKeyPath!));
            _key = new RsaSecurityKey(rsa)
            {
                KeyId = settings.KeyId,

                // Not the process-wide CryptoProviderFactory.Default: it caches signers by public key, so another key
                // instance with the same key material (another host in the process) would sign with this instance's
                // RSA after Dispose. A signer of its own is released after each assertion without disposing the RSA.
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            };
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            TokenType = "client-authentication+jwt",
            Issuer = settings.ClientId,
            Audience = settings.Issuer!.AbsoluteUri,
            Subject = new ClaimsIdentity([new Claim("sub", settings.ClientId), new Claim("jti", Guid.NewGuid().ToString())]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
    }
}
