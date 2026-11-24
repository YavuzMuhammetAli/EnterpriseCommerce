using System.Text.Json;
using EnterpriseCommerce.Inventory.Application.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.Infrastructure.ServiceClients;

/// <summary>
/// Calls <c>POST /internal/v1/catalog/tenants/{tenantId}/variant-verifications</c> at Catalog (ADR-027). Fails closed:
/// anything other than a well-formed <c>200</c> verdict raises <see cref="CatalogUnavailableException"/>. Never caches a
/// verdict. Never logs the body or the token.
/// </summary>
internal sealed partial class CatalogVariantVerifier(
    HttpClient httpClient,
    InventoryServiceTokenProvider tokens,
    IOptions<CatalogVerificationOptions> options,
    IOptions<ServiceClientOptions> serviceClient,
    ILogger<CatalogVariantVerifier> logger) : ICatalogVariantVerifier
{
    /// <summary>The token rule of Catalog's open product status set.</summary>
    private const int MaxStatusLength = 32;

    public async Task<CatalogVariantVerdict> VerifyAsync(Guid tenantId, Guid productId, Guid variantId, string? correlationId, CancellationToken cancellationToken)
    {
        using var result = await InternalApiCaller.PostAsync(
            httpClient, tokens, CatalogVerificationOptions.Scope, options.Value.BaseAddress, serviceClient.Value.IsSpecified,
            $"internal/v1/catalog/tenants/{tenantId:D}/variant-verifications", new { productId, variantId }, correlationId, cancellationToken);

        if (result.Failure != InternalCallFailure.None)
        {
            LogUnavailable(logger, result.Failure.ToString(), result.StatusCode ?? 0, result.Exception?.GetType().Name ?? string.Empty);
            throw new CatalogUnavailableException($"No variant verdict: {result.Failure}.", result.Exception ?? new InvalidOperationException(result.Failure.ToString()));
        }

        var root = result.Body!.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("found", out var found) || found.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Malformed();
        }

        if (!found.GetBoolean())
        {
            return CatalogVariantVerdict.NotFound;
        }

        // A found variant names its status (an open set, a PascalCase token), its sellability and its SKU; anything less
        // is not a verdict. The SKU's own rule is checked by the caller (SkuSnapshot), which fails closed as well.
        if (!root.TryGetProperty("productStatus", out var status)
            || status.ValueKind != JsonValueKind.String
            || status.GetString() is not { Length: > 0 and <= MaxStatusLength } statusValue
            || !char.IsAsciiLetterUpper(statusValue[0])
            || !statusValue.All(char.IsAsciiLetterOrDigit)
            || !root.TryGetProperty("variantActive", out var variantActive)
            || variantActive.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("sku", out var sku)
            || sku.ValueKind != JsonValueKind.String)
        {
            throw Malformed();
        }

        return new CatalogVariantVerdict(true, statusValue, variantActive.GetBoolean(), sku.GetString());

        CatalogUnavailableException Malformed()
        {
            LogUnavailable(logger, "Malformed", 200, string.Empty);
            return new CatalogUnavailableException("Catalog gave no well-formed verdict.");
        }
    }

    [LoggerMessage(
        EventId = 5210,
        Level = LogLevel.Warning,
        Message = "No variant verdict from Catalog ({Failure}, HTTP {StatusCode}, {ExceptionType}); item creation fails closed")]
    private static partial void LogUnavailable(ILogger logger, string failure, int statusCode, string exceptionType);
}
