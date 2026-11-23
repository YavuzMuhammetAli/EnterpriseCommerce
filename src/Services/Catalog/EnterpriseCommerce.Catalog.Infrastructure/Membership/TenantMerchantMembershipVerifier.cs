using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCommerce.Catalog.Application.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Catalog.Infrastructure.Membership;

/// <summary>
/// Calls <c>POST /internal/v1/tenants/{tenantId}/membership-verifications</c> at Tenant &amp; Merchant (ADR-025). Fails
/// closed: anything other than a well-formed <c>200</c> verdict raises <see cref="MembershipUnavailableException"/>.
/// Never caches a verdict. Never logs the subject, the request body or the token.
/// </summary>
internal sealed partial class TenantMerchantMembershipVerifier(
    HttpClient httpClient,
    CatalogServiceTokenProvider tokens,
    IOptions<MembershipVerificationOptions> options,
    ILogger<TenantMerchantMembershipVerifier> logger) : IMembershipVerifier
{
    private const string CorrelationHeader = "X-Correlation-ID";

    /// <summary>The token rule of Tenant &amp; Merchant's open role set: a PascalCase token of at most 32 characters.</summary>
    private const int MaxRoleLength = 32;

    public async Task<MembershipVerdict> VerifyAsync(Guid tenantId, string subjectId, string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        var settings = options.Value;
        if (!settings.IsSpecified)
        {
            LogNotConfigured(logger);
            throw new MembershipUnavailableException("The membership verification integration is not configured.");
        }

        // One retry, only after a 401: the cached service token may have been revoked or expired early.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var accessToken = await GetTokenAsync(cancellationToken);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                new Uri(settings.BaseAddress!, $"internal/v1/tenants/{tenantId:D}/membership-verifications"))
            {
                Content = JsonContent.Create(new { subjectId }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (correlationId is { Length: > 0 })
            {
                request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
            }

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                LogNotReached(logger, exception.GetType().Name);
                throw new MembershipUnavailableException("Tenant & Merchant could not be reached.", exception);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
                {
                    tokens.Invalidate();
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    LogUnexpectedStatus(logger, (int)response.StatusCode);
                    throw new MembershipUnavailableException($"Tenant & Merchant answered {(int)response.StatusCode}.");
                }

                return await ReadVerdictAsync(response, cancellationToken);
            }
        }

        LogUnexpectedStatus(logger, (int)HttpStatusCode.Unauthorized);
        throw new MembershipUnavailableException("Tenant & Merchant refused the service token.");
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
            throw new MembershipUnavailableException("No service token for Tenant & Merchant.", exception);
        }
    }

    /// <summary>
    /// Reads <c>{ "active": true, "role": "..." }</c> or <c>{ "active": false, "role": null }</c>. An active verdict
    /// without a well-formed role is not a verdict: it fails closed rather than granting anything.
    /// </summary>
    private async Task<MembershipVerdict> ReadVerdictAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("active", out var active) || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Malformed();
            }

            if (!active.GetBoolean())
            {
                return MembershipVerdict.Inactive;
            }

            if (!root.TryGetProperty("role", out var role)
                || role.ValueKind != JsonValueKind.String
                || role.GetString() is not { Length: > 0 and <= MaxRoleLength } value
                || !char.IsAsciiLetterUpper(value[0])
                || !value.All(char.IsAsciiLetterOrDigit))
            {
                throw Malformed();
            }

            return new MembershipVerdict(true, value);
        }
        catch (JsonException exception)
        {
            LogMalformed(logger);
            throw new MembershipUnavailableException("Tenant & Merchant's verdict is not valid JSON.", exception);
        }

        MembershipUnavailableException Malformed()
        {
            LogMalformed(logger);
            return new MembershipUnavailableException("Tenant & Merchant gave no well-formed verdict.");
        }
    }

    [LoggerMessage(EventId = 4200, Level = LogLevel.Error, Message = "Membership verification is not configured; merchant requests fail closed")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(EventId = 4201, Level = LogLevel.Warning, Message = "No service token for Tenant & Merchant ({ExceptionType}); merchant requests fail closed")]
    private static partial void LogTokenUnavailable(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Warning, Message = "Tenant & Merchant not reached ({ExceptionType}); merchant requests fail closed")]
    private static partial void LogNotReached(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Warning, Message = "Membership verification answered HTTP {StatusCode}; merchant requests fail closed")]
    private static partial void LogUnexpectedStatus(ILogger logger, int statusCode);

    [LoggerMessage(EventId = 4204, Level = LogLevel.Warning, Message = "Membership verification gave no well-formed verdict; merchant requests fail closed")]
    private static partial void LogMalformed(ILogger logger);
}
