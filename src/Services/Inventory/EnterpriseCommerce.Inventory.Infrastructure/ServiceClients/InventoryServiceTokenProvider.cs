using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Inventory.Infrastructure.ServiceClients;

/// <summary>
/// Obtains and caches client-credentials access tokens of <c>svc-inventory</c>, one per scope, authenticating at Identity
/// with a client assertion (<c>private_key_jwt</c>, typed <c>client-authentication+jwt</c>), as Catalog and Tenant &amp;
/// Merchant do. A token (never a verdict) is renewed a minute before it expires and dropped after a <c>401</c>. It is
/// never logged.
/// </summary>
/// <remarks>
/// A copy of Catalog's provider, with the signing-key fix of Phase 4 (finding F1) and a cache per scope. A third copy
/// of this code makes it a candidate for a building block, deferred to keep Phase 5 from changing two more services
/// (PHASE_5_INVENTORY.md, deferred work).
/// </remarks>
internal sealed class InventoryServiceTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<ServiceClientOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    public const string HttpClientName = "inventory-service-token";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset RenewAt)> _cached = new(StringComparer.Ordinal);
    private RsaSecurityKey? _key;

    public async Task<string> GetAsync(string scope, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (_cached.TryGetValue(scope, out var current) && current.RenewAt > timeProvider.GetUtcNow())
        {
            return current.Value;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached.TryGetValue(scope, out var refreshed) && refreshed.RenewAt > timeProvider.GetUtcNow())
            {
                return refreshed.Value;
            }

            var settings = options.Value;
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = scope,
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

            _cached[scope] = (value, timeProvider.GetUtcNow().AddSeconds(Math.Max(lifetime - 60, 0)));
            return value;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Invalidate(string scope) => _cached.TryRemove(scope, out _);

    public void Dispose()
    {
        _lock.Dispose();
        _key?.Rsa?.Dispose();
    }

    private string CreateAssertion(ServiceClientOptions settings)
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
                // RSA after Dispose (Phase 4 finding F1). A signer of its own is released after each assertion.
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
