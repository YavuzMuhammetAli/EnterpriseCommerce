using System.Text.Json;
using EnterpriseCommerce.Inventory.Application.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.Infrastructure.ServiceClients;

/// <summary>
/// Calls <c>POST /internal/v1/tenants/{tenantId}/membership-verifications</c> at Tenant &amp; Merchant (ADR-025), as
/// Catalog does. Fails closed: anything other than a well-formed <c>200</c> verdict raises
/// <see cref="MembershipUnavailableException"/>. Never caches a verdict. Never logs the subject, the body or the token.
/// </summary>
internal sealed partial class TenantMerchantMembershipVerifier(
    HttpClient httpClient,
    InventoryServiceTokenProvider tokens,
    IOptions<MembershipVerificationOptions> options,
    IOptions<ServiceClientOptions> serviceClient,
    ILogger<TenantMerchantMembershipVerifier> logger) : IMembershipVerifier
{
    /// <summary>The token rule of Tenant &amp; Merchant's open role set: a PascalCase token of at most 32 characters.</summary>
    private const int MaxRoleLength = 32;

    public async Task<MembershipVerdict> VerifyAsync(Guid tenantId, string subjectId, string? correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);

        using var result = await InternalApiCaller.PostAsync(
            httpClient, tokens, MembershipVerificationOptions.Scope, options.Value.BaseAddress, serviceClient.Value.IsSpecified,
            $"internal/v1/tenants/{tenantId:D}/membership-verifications", new { subjectId }, correlationId, cancellationToken);

        if (result.Failure != InternalCallFailure.None)
        {
            LogUnavailable(logger, result.Failure.ToString(), result.StatusCode ?? 0, result.Exception?.GetType().Name ?? string.Empty);
            throw new MembershipUnavailableException($"No membership verdict: {result.Failure}.", result.Exception ?? new InvalidOperationException(result.Failure.ToString()));
        }

        // { "active": true, "role": "..." } or { "active": false, "role": null }. An active verdict without a
        // well-formed role is not a verdict: it fails closed rather than granting anything.
        var root = result.Body!.RootElement;
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

        MembershipUnavailableException Malformed()
        {
            LogUnavailable(logger, "Malformed", 200, string.Empty);
            return new MembershipUnavailableException("Tenant & Merchant gave no well-formed verdict.");
        }
    }

    [LoggerMessage(
        EventId = 5200,
        Level = LogLevel.Warning,
        Message = "No membership verdict from Tenant & Merchant ({Failure}, HTTP {StatusCode}, {ExceptionType}); merchant requests fail closed")]
    private static partial void LogUnavailable(ILogger logger, string failure, int statusCode, string exceptionType);
}
