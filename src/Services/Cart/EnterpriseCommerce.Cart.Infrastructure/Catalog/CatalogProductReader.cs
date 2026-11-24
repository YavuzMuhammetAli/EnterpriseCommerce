using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Cart.Application.Catalog;
using EnterpriseCommerce.Cart.Application.Common;
using EnterpriseCommerce.Cart.Domain.Carts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Cart.Infrastructure.Catalog;

/// <summary>Catalog's address and the bounds of its reads (ADR-029 section 5). Configuration section <c>Cart:Catalog</c>.</summary>
internal sealed class CatalogReadOptions
{
    public const string SectionName = "Cart:Catalog";

    /// <summary>Catalog's own address (not the Gateway's). Required outside Development, where it must use HTTPS.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>The bound of one read, connection included. 100 milliseconds to 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The largest product answer accepted. 64 KiB to 8 MiB.</summary>
    public int MaxResponseBytes { get; set; } = 1024 * 1024;

    public IEnumerable<string> Validate(bool isDevelopment)
    {
        if (BaseAddress is null)
        {
            if (!isDevelopment)
            {
                yield return $"{SectionName}:BaseAddress is required outside Development.";
            }
        }
        else if (!BaseAddress.IsAbsoluteUri || (!isDevelopment && BaseAddress.Scheme != Uri.UriSchemeHttps) || !BaseAddress.AbsolutePath.EndsWith('/'))
        {
            yield return $"{SectionName}:BaseAddress must be an absolute URI ending with '/', using HTTPS outside Development.";
        }

        if (Timeout < TimeSpan.FromMilliseconds(100) || Timeout > TimeSpan.FromSeconds(10))
        {
            yield return $"{SectionName}:Timeout must be 100 milliseconds to 10 seconds.";
        }

        if (MaxResponseBytes is < 64 * 1024 or > 8 * 1024 * 1024)
        {
            yield return $"{SectionName}:MaxResponseBytes must be 64 KiB to 8 MiB.";
        }
    }
}

/// <summary>
/// Reads <c>GET /api/v1/catalog/products/{productId}</c>, Catalog's anonymous public read (CATALOG_API_CONTRACT.md
/// section 2.2), and maps it strictly (ADR-029 section 5).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A <c>200</c> must be the documented shape for exactly that product, with every value inside the domain's
/// bounds, or the whole answer is refused.</item>
/// <item>Only a <c>404</c> whose problem type is Catalog's <c>product-not-found</c> means "not published"; any other
/// <c>404</c> looks like a routing error and fails closed.</item>
/// <item>Everything else, including a timeout, raises <see cref="CatalogUnavailableException"/>. There is no retry.</item>
/// <item>The caller's cancellation passes through as cancellation, never as an outage.</item>
/// </list>
/// Logs carry the failure kind and status only: no product, customer or body.
/// </remarks>
internal sealed partial class CatalogProductReader(HttpClient httpClient, IOptions<CatalogReadOptions> options, ILogger<CatalogProductReader> logger)
    : ICatalogProductReader
{
    public const string ProductNotFoundType = "https://errors.enterprisecommerce.example/catalog/product-not-found";
    private const string CorrelationHeader = "X-Correlation-ID";

    public async Task<CatalogProduct?> ReadAsync(Guid productId, string? correlationId, CancellationToken cancellationToken)
    {
        if (options.Value.BaseAddress is not { } baseAddress)
        {
            throw Unavailable("NotConfigured", 0);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, $"api/v1/catalog/products/{productId:D}"));
        request.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrEmpty(correlationId))
        {
            request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);
        }

        HttpResponseMessage response;
        string body;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or TimeoutException or IOException)
        {
            throw Unavailable(exception is TaskCanceledException or TimeoutException ? "Timeout" : "NotReached", 0, exception);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound && IsProductNotFound(response, body))
            {
                CartMetrics.RecordCatalogRead("not_published");
                return null;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw Unavailable("UnexpectedStatus", (int)response.StatusCode);
            }

            var product = Map(productId, body) ?? throw Unavailable("Malformed", 200);
            CartMetrics.RecordCatalogRead("found");
            return product;
        }
    }

    private static bool IsProductNotFound(HttpResponseMessage response, string body)
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/problem+json")
        {
            return false;
        }

        try
        {
            using var problem = JsonDocument.Parse(body);
            return problem.RootElement.ValueKind == JsonValueKind.Object
                && problem.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == ProductNotFoundType;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The product, or <c>null</c> when the body is not the documented shape for <paramref name="productId"/>.</summary>
    internal static CatalogProduct? Map(Guid productId, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGuid(root, "id", out var id) || id != productId
                || !TryGuid(root, "tenantId", out var tenantId)
                || !TryString(root, "name", out var name)
                || !root.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            ProductImage? image = null;
            if (media.GetArrayLength() > 0)
            {
                var first = media[0];
                if (first.ValueKind != JsonValueKind.Object || !TryString(first, "url", out var url) || !TryOptionalString(first, "altText", out var altText)
                    || !ProductImage.IsValid(url, altText))
                {
                    return null;
                }

                image = new ProductImage(url, altText);
            }

            var active = new Dictionary<Guid, ProductSnapshot>();
            foreach (var variant in variants.EnumerateArray())
            {
                if (variant.ValueKind != JsonValueKind.Object
                    || !TryGuid(variant, "id", out var variantId)
                    || !TryString(variant, "sku", out var sku)
                    || !variant.TryGetProperty("isActive", out var isActive) || isActive.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !TryOptions(variant, out var variantOptions)
                    || !TryPrice(variant, out var price))
                {
                    return null;
                }

                if (!isActive.GetBoolean())
                {
                    continue;
                }

                var snapshot = ProductSnapshot.TryCreate(productId, variantId, tenantId, name, sku, variantOptions, image, price);
                if (snapshot is null || !active.TryAdd(variantId, snapshot))
                {
                    return null;
                }
            }

            return new CatalogProduct(productId, tenantId, active);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGuid(JsonElement element, string name, out Guid value)
    {
        value = Guid.Empty;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParseExact(property.GetString(), "D", out value)
            && value != Guid.Empty;
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }

    private static bool TryOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static bool TryOptions(JsonElement variant, out List<VariantOption> options)
    {
        options = [];
        if (!variant.TryGetProperty("options", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var option in array.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object || !TryString(option, "key", out var key) || !TryString(option, "value", out var value))
            {
                return false;
            }

            options.Add(new VariantOption(key, value));
        }

        return true;
    }

    private static bool TryPrice(JsonElement variant, out Money? price)
    {
        price = null;
        return variant.TryGetProperty("price", out var element)
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("amount", out var amount)
            && amount.ValueKind == JsonValueKind.Number
            && amount.TryGetDecimal(out var value)
            && TryString(element, "currency", out var currency)
            && Money.TryCreate(value, currency, out price);
    }

    private CatalogUnavailableException Unavailable(string failure, int statusCode, Exception? exception = null)
    {
        CartMetrics.RecordCatalogRead("failed");
        LogUnavailable(logger, failure, statusCode, exception?.GetType().Name ?? string.Empty);
        return exception is null
            ? new CatalogUnavailableException($"Catalog gave no usable answer: {failure}.")
            : new CatalogUnavailableException($"Catalog gave no usable answer: {failure}.", exception);
    }

    [LoggerMessage(
        EventId = 5210,
        Level = LogLevel.Warning,
        Message = "No usable product answer from Catalog ({Failure}, HTTP {StatusCode}, {ExceptionType}); the command fails closed")]
    private static partial void LogUnavailable(ILogger logger, string failure, int statusCode, string exceptionType);
}
