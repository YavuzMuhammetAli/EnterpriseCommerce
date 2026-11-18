using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;

/// <summary>
/// The Notification integration (<c>Identity:Notification</c>, ADR-018 section 3). Identity authenticates as the
/// service client <see cref="ClientId"/> with <c>private_key_jwt</c> and calls Notification's internal endpoint.
/// Nothing is stored or logged from these settings except the addresses.
/// </summary>
internal sealed class NotificationDeliveryOptions
{
    public const string SectionName = "Identity:Notification";

    /// <summary>Notification's internal base address (https).</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The internal address of Identity's own token endpoint (ADR-017 section 4).</summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>The audience of the client assertion: the Identity issuer.</summary>
    public Uri? Issuer { get; set; }

    public string ClientId { get; set; } = "svc-identity";

    /// <summary>A PEM file with the client's RSA private key, from a protected store. Never committed.</summary>
    public string? PrivateKeyPath { get; set; }

    public string Scope { get; set; } = "svc.notification.security-email";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    public bool IsConfigured =>
        BaseAddress is { IsAbsoluteUri: true, Scheme: "https" }
        && TokenEndpoint is { IsAbsoluteUri: true, Scheme: "https" }
        && Issuer is { IsAbsoluteUri: true }
        && ClientId.StartsWith("svc-", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(PrivateKeyPath);
}

/// <summary>
/// Hands security emails to Notification over the internal contract <c>POST internal/v1/security-emails</c>
/// (documented in IDENTITY_API_CONTRACT.md). The delivery ID is the <c>Idempotency-Key</c>, so a retry after an
/// unknown outcome cannot send a second email. Never logs the link, the recipient or the request body.
/// </summary>
internal sealed partial class NotificationSecurityEmailDispatcher(
    HttpClient httpClient,
    NotificationServiceTokenProvider tokens,
    IOptions<NotificationDeliveryOptions> options,
    ILogger<NotificationSecurityEmailDispatcher> logger) : ISecurityEmailDispatcher
{
    public async Task<SecurityEmailDispatchOutcome> DispatchAsync(SecurityEmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var settings = options.Value;

        if (!settings.IsConfigured)
        {
            LogNotConfigured(logger, message.DeliveryId);
            return SecurityEmailDispatchOutcome.PermanentFailure;
        }

        string accessToken;
        try
        {
            accessToken = await tokens.GetAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Nothing was sent to Notification yet.
            LogTokenUnavailable(logger, exception.GetType().Name, message.DeliveryId);
            return SecurityEmailDispatchOutcome.TransientFailure;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseAddress!, "internal/v1/security-emails"))
        {
            Content = JsonContent.Create(new
            {
                deliveryId = message.DeliveryId,
                purpose = message.Purpose.ToString(),
                recipient = message.RecipientEmail,
                link = message.Link.AbsoluteUri,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Idempotency-Key", message.DeliveryId.ToString());

        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                tokens.Invalidate();
            }

            var outcome = Classify(response.StatusCode);
            LogHandover(logger, message.DeliveryId, (int)response.StatusCode, outcome);
            return outcome;
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            LogNotReached(logger, message.DeliveryId);
            return SecurityEmailDispatchOutcome.TransientFailure;
        }
        catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Sent, but no answer: Notification may have accepted it.
            LogUnknownOutcome(logger, message.DeliveryId, exception.GetType().Name);
            return SecurityEmailDispatchOutcome.Unknown;
        }
    }

    internal static SecurityEmailDispatchOutcome Classify(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => SecurityEmailDispatchOutcome.Delivered,
        409 => SecurityEmailDispatchOutcome.Delivered, // the same Idempotency-Key was already accepted
        401 or 408 or 429 => SecurityEmailDispatchOutcome.TransientFailure,
        >= 500 => SecurityEmailDispatchOutcome.TransientFailure,
        _ => SecurityEmailDispatchOutcome.PermanentFailure,
    };

    [LoggerMessage(EventId = 2120, Level = LogLevel.Error, Message = "Notification delivery is not configured; security email delivery {DeliveryId} failed")]
    private static partial void LogNotConfigured(ILogger logger, Guid deliveryId);

    [LoggerMessage(EventId = 2121, Level = LogLevel.Warning, Message = "No service token for Notification ({ExceptionType}); delivery {DeliveryId} not sent")]
    private static partial void LogTokenUnavailable(ILogger logger, string exceptionType, Guid deliveryId);

    [LoggerMessage(EventId = 2122, Level = LogLevel.Information, Message = "Security email delivery {DeliveryId} handed to Notification: HTTP {StatusCode}, {Outcome}")]
    private static partial void LogHandover(ILogger logger, Guid deliveryId, int statusCode, SecurityEmailDispatchOutcome outcome);

    [LoggerMessage(EventId = 2123, Level = LogLevel.Warning, Message = "Notification not reachable; delivery {DeliveryId} not sent")]
    private static partial void LogNotReached(ILogger logger, Guid deliveryId);

    [LoggerMessage(EventId = 2124, Level = LogLevel.Warning, Message = "Unknown handover outcome for delivery {DeliveryId} ({ExceptionType})")]
    private static partial void LogUnknownOutcome(ILogger logger, Guid deliveryId, string exceptionType);
}

/// <summary>
/// Obtains and caches a client-credentials access token for Notification, authenticating with a client assertion
/// (<c>private_key_jwt</c>, typed <c>client-authentication+jwt</c>, Phase 2.2 finding I2).
/// </summary>
internal sealed class NotificationServiceTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<NotificationDeliveryOptions> options,
    TimeProvider timeProvider) : IDisposable
{
    public const string HttpClientName = "notification-service-token";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private RsaSecurityKey? _key;
    private (string Token, DateTimeOffset ExpiresAt)? _cached;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } current && current.ExpiresAt > timeProvider.GetUtcNow())
        {
            return current.Token;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } refreshed && refreshed.ExpiresAt > timeProvider.GetUtcNow())
            {
                return refreshed.Token;
            }

            var settings = options.Value;
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(
                settings.TokenEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["scope"] = settings.Scope,
                    ["client_id"] = settings.ClientId,
                    ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                    ["client_assertion"] = CreateAssertion(settings),
                }),
                cancellationToken);
            response.EnsureSuccessStatusCode();

            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var token = json.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("The token response has no access token.");
            var lifetime = json.RootElement.TryGetProperty("expires_in", out var expiresIn) ? expiresIn.GetInt32() : 300;

            // Renew a minute early so that a token never expires in flight.
            _cached = (token, timeProvider.GetUtcNow().AddSeconds(Math.Max(lifetime - 60, 0)));
            return token;
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

    private string CreateAssertion(NotificationDeliveryOptions settings)
    {
        if (_key is null)
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(settings.PrivateKeyPath!));
            _key = new RsaSecurityKey(rsa);
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
