using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Identity;

/// <summary>
/// The Identity integration of invitation acceptance (<c>TenantMerchant:IdentityVerification</c>, ADR-021). Tenant &amp;
/// Merchant authenticates as the service client <see cref="ClientId"/> with <c>private_key_jwt</c> and calls Identity's
/// internal email verification endpoint. The private key is read from a file outside the repository.
/// </summary>
internal sealed class IdentityVerificationOptions
{
    public const string SectionName = "TenantMerchant:IdentityVerification";

    /// <summary>The scope of Identity's internal verification API (IDENTITY_API_CONTRACT.md section 2.13).</summary>
    public const string Scope = "svc.identity.account-verification";

    /// <summary>Identity's internal base address.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Identity's token endpoint. Defaults to <c>connect/token</c> under <see cref="BaseAddress"/>.</summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>The audience of the client assertion: the Identity issuer. Defaults to <c>TenantMerchant:Authentication:Issuer</c>.</summary>
    public Uri? Issuer { get; set; }

    public string ClientId { get; set; } = "svc-tenant-merchant";

    /// <summary>A PEM file with the client's RSA private key, from a protected store. Never committed.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The <c>kid</c> of the key in the client's registered JWKS, if it has one.</summary>
    public string? KeyId { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether any value is set. A partial configuration is an error, not a disabled integration.</summary>
    public bool IsSpecified => BaseAddress is not null || TokenEndpoint is not null || !string.IsNullOrWhiteSpace(PrivateKeyPath);

    public Uri? EffectiveTokenEndpoint => TokenEndpoint ?? (BaseAddress is { IsAbsoluteUri: true } address ? new Uri(address, "connect/token") : null);

    /// <summary>The configuration errors. Outside Development the integration is required and must use https.</summary>
    public IEnumerable<string> Validate(bool isDevelopment)
    {
        if (!IsSpecified)
        {
            if (!isDevelopment)
            {
                yield return $"{SectionName} is required: invitation acceptance verifies the invitee's email with Identity.";
            }

            yield break;
        }

        foreach (var (name, value) in new[] { ("BaseAddress", BaseAddress), ("TokenEndpoint", EffectiveTokenEndpoint), ("Issuer", Issuer) })
        {
            if (value is not { IsAbsoluteUri: true } || (!isDevelopment && value.Scheme != Uri.UriSchemeHttps))
            {
                yield return $"{SectionName}:{name} must be an absolute {(isDevelopment ? string.Empty : "https ")}URI.";
            }
        }

        if (!ClientId.StartsWith("svc-", StringComparison.Ordinal) || ClientId.Length == "svc-".Length)
        {
            yield return $"{SectionName}:ClientId must be a service client (svc- prefix).";
        }

        if (string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            yield return $"{SectionName}:PrivateKeyPath is required.";
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromSeconds(30))
        {
            yield return $"{SectionName}:Timeout must be between 0 and 30 seconds.";
        }
    }
}

/// <summary>
/// Calls <c>POST /internal/v1/accounts/{accountId}/email-verification</c> at Identity. Fails closed: anything other than
/// one of the four documented answers or a <c>404</c> raises <see cref="IdentityUnavailableException"/>. Never logs the
/// email address, the request body or the token.
/// </summary>
internal sealed partial class IdentityAccountVerifier(
    HttpClient httpClient,
    IdentityServiceTokenProvider tokens,
    IOptions<IdentityVerificationOptions> options,
    ILogger<IdentityAccountVerifier> logger) : IIdentityAccountVerifier
{
    public async Task<IdentityEmailCheck> CheckEmailAsync(string subjectId, string email, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var settings = options.Value;
        if (!settings.IsSpecified)
        {
            LogNotConfigured(logger);
            throw new IdentityUnavailableException("The Identity verification integration is not configured.");
        }

        // One retry, only after a 401: the cached service token may have been revoked or expired early.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var accessToken = await GetTokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(settings.BaseAddress!, $"internal/v1/accounts/{Uri.EscapeDataString(subjectId)}/email-verification"))
            {
                Content = JsonContent.Create(new { email }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                LogNotReached(logger, exception.GetType().Name);
                throw new IdentityUnavailableException("Identity could not be reached.", exception);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    tokens.Invalidate();
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    LogAnswered(logger, subjectId, IdentityEmailCheck.UnknownAccount);
                    return IdentityEmailCheck.UnknownAccount;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    LogUnexpectedStatus(logger, (int)response.StatusCode);
                    throw new IdentityUnavailableException($"Identity answered {(int)response.StatusCode}.");
                }

                var check = await ReadAnswerAsync(response, cancellationToken);
                LogAnswered(logger, subjectId, check);
                return check;
            }
        }

        LogUnexpectedStatus(logger, (int)HttpStatusCode.Unauthorized);
        throw new IdentityUnavailableException("Identity refused the service token.");
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await tokens.GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogTokenUnavailable(logger, exception.GetType().Name);
            throw new IdentityUnavailableException("No service token for Identity.", exception);
        }
    }

    private static async Task<IdentityEmailCheck> ReadAnswerAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return json.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String
                ? result.GetString() switch
                {
                    "Match" => IdentityEmailCheck.Match,
                    "Mismatch" => IdentityEmailCheck.Mismatch,
                    "Unverified" => IdentityEmailCheck.Unverified,
                    "Inactive" => IdentityEmailCheck.Inactive,
                    _ => throw new IdentityUnavailableException("Identity gave an unknown verification answer."),
                }
                : throw new IdentityUnavailableException("Identity gave no verification answer.");
        }
        catch (JsonException exception)
        {
            throw new IdentityUnavailableException("Identity's verification answer is not valid JSON.", exception);
        }
    }

    [LoggerMessage(EventId = 3230, Level = LogLevel.Error, Message = "Identity verification is not configured; invitation acceptance fails closed")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 3231, Level = LogLevel.Warning, Message = "No service token for Identity ({ExceptionType}); invitation acceptance fails closed")]
    private static partial void LogTokenUnavailable(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3232, Level = LogLevel.Warning, Message = "Identity not reached ({ExceptionType}); invitation acceptance fails closed")]
    private static partial void LogNotReached(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 3233, Level = LogLevel.Warning, Message = "Identity verification answered HTTP {StatusCode}; invitation acceptance fails closed")]
    private static partial void LogUnexpectedStatus(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 3234, Level = LogLevel.Information, Message = "Identity verification for subject {Subject}: {Result}")]
    private static partial void LogAnswered(ILogger logger, string subject, IdentityEmailCheck result);
}

/// <summary>
/// Obtains and caches a client-credentials access token for Identity's internal API, authenticating with a client
/// assertion (<c>private_key_jwt</c>, typed <c>client-authentication+jwt</c>). The token is renewed a minute before it
/// expires and dropped after a <c>401</c>. It is never logged.
/// </summary>
internal sealed class IdentityServiceTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<IdentityVerificationOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    public const string HttpClientName = "identity-service-token";

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
                ["scope"] = IdentityVerificationOptions.Scope,
                ["client_id"] = settings.ClientId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = CreateAssertion(settings),
            });
            using var response = await client.PostAsync(settings.EffectiveTokenEndpoint, form, cancellationToken);
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

    private string CreateAssertion(IdentityVerificationOptions settings)
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
