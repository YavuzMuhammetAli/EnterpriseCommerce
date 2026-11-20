using System.Security.Claims;
using System.Security.Cryptography;
using EnterpriseCommerce.Bff.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Bff.Protocol;

/// <summary>
/// Creates <c>private_key_jwt</c> client assertions (RFC 7523, ADR-016 section 2) in the shape Identity accepts:
/// explicitly typed <c>client-authentication+jwt</c> (OpenIddict 7.9 rejects untyped assertions, finding I2),
/// <c>iss</c> = <c>sub</c> = client ID, <c>aud</c> = the issuer, a unique <c>jti</c> and a two-minute lifetime.
/// </summary>
internal sealed class ClientAssertionFactory : IDisposable
{
    public const string AssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    public const string TokenType = "client-authentication+jwt";

    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly BffSettings _settings;
    private readonly TimeProvider _time;
    private readonly AsymmetricAlgorithm _key;
    private readonly SigningCredentials _credentials;
    private readonly JsonWebTokenHandler _handler = new();

    public ClientAssertionFactory(IOptions<BffSettings> settings, TimeProvider time)
    {
        _settings = settings.Value;
        _time = time;
        var pem = _settings.ClientKey.PrivateKeyPem is { Length: > 0 } inline
            ? inline
            : File.ReadAllText(Environment.ExpandEnvironmentVariables(_settings.ClientKey.PrivateKeyPath!));
        (_key, _credentials) = LoadKey(pem, _settings.ClientKey.KeyId);
    }

    public string Create()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = TokenType,
            Issuer = _settings.ClientId,
            Audience = _settings.AuthorityUri.AbsoluteUri,
            Subject = new ClaimsIdentity([new Claim("sub", _settings.ClientId), new Claim("jti", Guid.NewGuid().ToString("N"))]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(Lifetime),
            SigningCredentials = _credentials,
        });
    }

    internal static (AsymmetricAlgorithm Key, SigningCredentials Credentials) LoadKey(string pem, string keyId)
    {
        if (pem.Contains("EC PRIVATE KEY", StringComparison.Ordinal) || TryImportEc(pem, out _))
        {
            var ec = ECDsa.Create();
            ec.ImportFromPem(pem);
            var algorithm = ec.KeySize switch
            {
                256 => SecurityAlgorithms.EcdsaSha256,
                384 => SecurityAlgorithms.EcdsaSha384,
                _ => SecurityAlgorithms.EcdsaSha512,
            };
            return (ec, new SigningCredentials(new ECDsaSecurityKey(ec) { KeyId = keyId }, algorithm));
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        if (rsa.KeySize < 2048)
        {
            rsa.Dispose();
            throw new InvalidOperationException("The BFF client key must be an RSA key of at least 2048 bits or an EC key.");
        }

        return (rsa, new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = keyId }, SecurityAlgorithms.RsaSha256));
    }

    private static bool TryImportEc(string pem, out ECDsa? key)
    {
        key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            key.Dispose();
            key = null;
        }
    }

    public void Dispose() => _key.Dispose();
}
