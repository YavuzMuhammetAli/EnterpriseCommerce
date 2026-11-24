using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

/// <summary>
/// A stand-in for Catalog's public product read (CATALOG_API_CONTRACT.md section 2.2), behind Cart's Catalog client. It
/// answers exactly as Catalog does: the public product shape for a published product, and the typed
/// <c>product-not-found</c> problem otherwise. Tests can also make it fail, stall or answer garbage.
/// </summary>
public sealed class StubCatalog : HttpMessageHandler
{
    public static readonly Uri Address = new("https://catalog.test/");

    private readonly ConcurrentDictionary<Guid, JsonObject> _products = new();
    private int _calls;

    /// <summary>When set, every read answers this status (no body).</summary>
    public HttpStatusCode? FailWith { get; set; }

    /// <summary>When set, every read answers this raw body with 200.</summary>
    public string? RawAnswer { get; set; }

    /// <summary>A delay before every answer, honouring the client's cancellation.</summary>
    public TimeSpan Delay { get; set; }

    public int CallCount => Volatile.Read(ref _calls);

    public string? LastCorrelationId { get; private set; }

    public bool LastHadAuthorization { get; private set; }

    /// <summary>Publishes a product with one variant per entry; returns the product ID.</summary>
    public Guid Publish(Guid tenantId, params StubVariant[] variants)
    {
        var productId = Guid.CreateVersion7();
        Set(productId, tenantId, variants);
        return productId;
    }

    /// <summary>Publishes or replaces a product.</summary>
    public void Set(Guid productId, Guid tenantId, params StubVariant[] variants) =>
        _products[productId] = new JsonObject
        {
            ["id"] = productId.ToString(),
            ["tenantId"] = tenantId.ToString(),
            ["slug"] = $"product-{productId:N}",
            ["name"] = "Blue shirt",
            ["shortDescription"] = null,
            ["description"] = null,
            ["brand"] = null,
            ["categories"] = new JsonArray(),
            ["attributes"] = new JsonArray(),
            ["variants"] = new JsonArray([.. variants.Select(variant => (JsonNode)new JsonObject
            {
                ["id"] = variant.Id.ToString(),
                ["sku"] = variant.Sku,
                ["options"] = new JsonArray(new JsonObject { ["key"] = "size", ["value"] = "M" }),
                ["price"] = new JsonObject { ["amount"] = variant.Amount, ["currency"] = variant.Currency },
                ["isActive"] = variant.IsActive,
            })]),
            ["media"] = new JsonArray(new JsonObject { ["id"] = Guid.CreateVersion7().ToString(), ["url"] = "https://cdn.example.com/a.jpg", ["altText"] = "Front", ["contentType"] = "image/jpeg" }),
            ["publishedAt"] = "2026-11-23T10:00:00+00:00",
        };

    /// <summary>Unpublishes (or archives) a product: it reads as <c>product-not-found</c> from now on.</summary>
    public void Unpublish(Guid productId) => _products.TryRemove(productId, out _);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        LastCorrelationId = request.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.Single() : null;
        LastHadAuthorization = request.Headers.Authorization is not null;

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        if (FailWith is { } status)
        {
            return new HttpResponseMessage(status);
        }

        if (RawAnswer is { } raw)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        }

        var path = request.RequestUri!.AbsolutePath;
        const string prefix = "/api/v1/catalog/products/";
        if (request.Method != HttpMethod.Get || !path.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParse(path[prefix.Length..], out var productId))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (_products.TryGetValue(productId, out var product))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(product.ToJsonString(), Encoding.UTF8, "application/json") };
        }

        var problem = JsonSerializer.Serialize(new
        {
            type = "https://errors.enterprisecommerce.example/catalog/product-not-found",
            title = "The product was not found.",
            status = 404,
        });
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(problem, Encoding.UTF8, "application/problem+json") };
    }
}

/// <summary>A variant of a stub product.</summary>
public sealed record StubVariant(Guid Id, decimal Amount = 19.99m, string Currency = "EUR", bool IsActive = true, string Sku = "BS-M")
{
    public static StubVariant New(decimal amount = 19.99m, string currency = "EUR", bool isActive = true) => new(Guid.CreateVersion7(), amount, currency, isActive);
}
