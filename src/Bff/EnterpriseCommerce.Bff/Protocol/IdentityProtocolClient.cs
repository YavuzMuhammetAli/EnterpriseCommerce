using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace EnterpriseCommerce.Bff.Protocol;

/// <summary>How a refresh request ended, from the BFF's point of view (ADR-016 section 4).</summary>
internal enum RefreshOutcome
{
    Success,

    /// <summary>Identity rejected the refresh token (revoked, reused, stale security version): the session ends.</summary>
    InvalidGrant,

    /// <summary>Identity rejected the client itself: never retried; an operational alert.</summary>
    ClientRejected,

    /// <summary>The request may have reached Identity, but no result is known. The refresh token is never presented again.</summary>
    Unknown,

    /// <summary>The request provably never left the BFF (connection could not be established). The token is still unused.</summary>
    NotSent,
}

internal sealed record RefreshResult(RefreshOutcome Outcome, SessionTokens? Tokens = null, string? Error = null);

/// <summary>The result of a call where only success, rejection or "unknown" matter.</summary>
internal enum CallOutcome
{
    Succeeded,
    Unauthorized,
    Forbidden,
    Unavailable,
}

/// <summary>
/// Back-channel calls to Identity with <c>private_key_jwt</c> client authentication: refresh, revocation and
/// logout-all. There is no retry policy anywhere on this client: a code exchange is never retried (finding I3), and a
/// refresh is retried only by the coordinator, and only when the request provably was not sent.
/// </summary>
internal sealed class IdentityProtocolClient(
    IHttpClientFactory httpClients,
    IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
    ClientAssertionFactory assertions,
    IOptions<BffSettings> settings,
    TimeProvider time)
{
    public const string HttpClientName = "identity";
    public const string LogoutAllPath = "api/v1/auth/logout-all";

    private readonly BffSettings _settings = settings.Value;

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var manager = oidcOptions.Get(OidcSetup.Scheme).ConfigurationManager
            ?? throw new InvalidOperationException("The OpenID Connect configuration manager is not initialized.");
        return await manager.GetConfigurationAsync(cancellationToken);
    }

    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await GetConfigurationAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Nothing was sent to the token endpoint.
            return new RefreshResult(RefreshOutcome.NotSent, Error: "metadata_unavailable");
        }

        var parameters = ClientParameters();
        parameters["grant_type"] = "refresh_token";
        parameters["refresh_token"] = refreshToken;

        using var timeout = new CancellationTokenSource(_settings.Refresh.RequestTimeout);
        var receivedAt = time.GetUtcNow();
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, configuration.TokenEndpoint) { Content = new FormUrlEncodedContent(parameters) };
            response = await Client().SendAsync(request, timeout.Token);
        }
        catch (HttpRequestException exception) when (IsProvablyNotSent(exception))
        {
            return new RefreshResult(RefreshOutcome.NotSent, Error: exception.HttpRequestError.ToString());
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            // Timeout after sending, connection reset or anything in between: the outcome is unknown.
            return new RefreshResult(RefreshOutcome.Unknown, Error: exception.GetType().Name);
        }

        using (response)
        {
            return await ClassifyAsync(response, receivedAt, timeout.Token);
        }
    }

    /// <summary>Revokes a refresh token at <c>/connect/revoke</c>; Identity then ends the whole authorization.</summary>
    public async Task<CallOutcome> RevokeAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await GetConfigurationAsync(cancellationToken);
            var endpoint = configuration.RevocationEndpoint;
            if (string.IsNullOrEmpty(endpoint))
            {
                return CallOutcome.Unavailable;
            }

            var parameters = ClientParameters();
            parameters["token"] = token;
            parameters["token_type_hint"] = "refresh_token";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_settings.Refresh.RequestTimeout);
            using var response = await Client().PostAsync(endpoint, new FormUrlEncodedContent(parameters), timeout.Token);
            return response.IsSuccessStatusCode ? CallOutcome.Succeeded : CallOutcome.Unavailable;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            return CallOutcome.Unavailable;
        }
    }

    /// <summary>Calls <c>POST /api/v1/auth/logout-all</c> with the session's bearer token. The user comes only from the token.</summary>
    public async Task<CallOutcome> LogoutAllAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_settings.Refresh.RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_settings.AuthorityUri, LogoutAllPath));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await Client().SendAsync(request, timeout.Token);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent or HttpStatusCode.OK => CallOutcome.Succeeded,
                HttpStatusCode.Unauthorized => CallOutcome.Unauthorized,
                HttpStatusCode.Forbidden => CallOutcome.Forbidden,
                _ => CallOutcome.Unavailable,
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return CallOutcome.Unavailable;
        }
    }

    /// <summary>Whether an access token is issued for this front end's audience (ADR-017 section 4).</summary>
    public bool HasExpectedAudience(string accessToken)
    {
        try
        {
            return new JsonWebToken(accessToken).Audiences.Contains(_settings.Audience, StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsProvablyNotSent(HttpRequestException exception) =>
        exception.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError;

    private async Task<RefreshResult> ClassifyAsync(HttpResponseMessage response, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode >= 500)
        {
            // Includes 503 from a ledger failure: the token may or may not have been redeemed (ADR-017 section 5).
            return new RefreshResult(RefreshOutcome.Unknown, Error: ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        JsonElement body;
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            body = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or OperationCanceledException or IOException)
        {
            return new RefreshResult(response.IsSuccessStatusCode ? RefreshOutcome.Unknown : RefreshOutcome.ClientRejected, Error: "unreadable_response");
        }

        if (response.IsSuccessStatusCode)
        {
            var access = String(body, "access_token");
            if (access is null || !body.TryGetProperty("expires_in", out var expiresIn) || !expiresIn.TryGetInt32(out var seconds))
            {
                return new RefreshResult(RefreshOutcome.Unknown, Error: "incomplete_response");
            }

            if (!HasExpectedAudience(access))
            {
                return new RefreshResult(RefreshOutcome.ClientRejected, Error: "unexpected_audience");
            }

            return new RefreshResult(RefreshOutcome.Success, new SessionTokens(
                access,
                String(body, "refresh_token"),
                String(body, "id_token"),
                receivedAt.AddSeconds(seconds),
                receivedAt));
        }

        var error = String(body, "error");
        return error == "invalid_grant"
            ? new RefreshResult(RefreshOutcome.InvalidGrant, Error: error)
            : new RefreshResult(RefreshOutcome.ClientRejected, Error: error ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private Dictionary<string, string> ClientParameters() => new()
    {
        ["client_id"] = _settings.ClientId,
        ["client_assertion_type"] = ClientAssertionFactory.AssertionType,
        ["client_assertion"] = assertions.Create(),
    };

    private HttpClient Client() => httpClients.CreateClient(HttpClientName);

    private static string? String(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
