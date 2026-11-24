using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnterpriseCommerce.Cart.Application.Catalog;
using EnterpriseCommerce.Cart.Infrastructure.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Cart.UnitTests.Infrastructure;

/// <summary>
/// The Catalog client against Catalog's documented public product read (CATALOG_API_CONTRACT.md section 2.2): the
/// mapping, and every failure that must fail closed (ADR-029 section 5).
/// </summary>
public sealed class CatalogProductReaderTests
{
    private static readonly Uri Catalog = new("https://catalog.test/");
    private static readonly Guid ProductId = Guid.CreateVersion7();
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid VariantM = Guid.CreateVersion7();
    private static readonly Guid VariantL = Guid.CreateVersion7();

    [Fact]
    public async Task APublishedProduct_IsMapped_ToOneSnapshotPerActiveVariant()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Product()));

        var product = await Reader(handler).ReadAsync(ProductId, "corr-1", TestContext.Current.CancellationToken);

        Assert.NotNull(product);
        Assert.Equal(TenantId, product.TenantId);
        var snapshot = Assert.Single(product.ActiveVariants).Value;
        Assert.Equal(VariantM, snapshot.VariantId);
        Assert.Equal(ProductId, snapshot.ProductId);
        Assert.Equal(TenantId, snapshot.TenantId);
        Assert.Equal("Blue shirt", snapshot.Name);
        Assert.Equal("BS-M", snapshot.Sku);
        Assert.Equal("size", Assert.Single(snapshot.Options).Key);
        Assert.Equal(19.99m, snapshot.UnitPrice.Amount);
        Assert.Equal("EUR", snapshot.UnitPrice.Currency);
        Assert.Equal("https://cdn.example.com/a.jpg", snapshot.Image!.Url);
        Assert.Equal("Front", snapshot.Image.AltText);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"https://catalog.test/api/v1/catalog/products/{ProductId:D}", request.RequestUri!.AbsoluteUri);
        Assert.Equal("corr-1", Assert.Single(request.Headers.GetValues("X-Correlation-ID")));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public async Task AProductWithoutMedia_HasNoImage()
    {
        var product = await Reader(new StubHandler(_ => Json(HttpStatusCode.OK, Product(media: new JsonArray())))).ReadAsync(ProductId, null, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(product!.ActiveVariants).Value.Image);
    }

    [Fact]
    public async Task CatalogsProductNotFound_MeansNotPublished()
    {
        var problem = new JsonObject { ["type"] = CatalogProductReader.ProductNotFoundType, ["title"] = "The product was not found.", ["status"] = 404 };

        var product = await Reader(new StubHandler(_ => Json(HttpStatusCode.NotFound, problem, "application/problem+json"))).ReadAsync(ProductId, null, TestContext.Current.CancellationToken);

        Assert.Null(product);
    }

    public static TheoryData<string> Outages() =>
    [
        "404 without a problem", "404 of another type", "500", "502", "503", "401", "302", "malformed JSON", "an array",
        "another product", "no tenant", "an empty tenant", "no name", "no variants", "no media", "a variant without price",
        "a string amount", "three decimals", "a zero price", "a lower-case currency", "a variant ID twice", "an http image",
        "a long name", "too many options", "an option without value", "a non-boolean isActive",
    ];

    [Theory]
    [MemberData(nameof(Outages))]
    public async Task AnyOtherAnswer_IsAnOutage_NeverANotFound(string answer)
    {
        var handler = new StubHandler(_ => answer switch
        {
            "404 without a problem" => new HttpResponseMessage(HttpStatusCode.NotFound),
            "404 of another type" => Json(HttpStatusCode.NotFound, new JsonObject { ["type"] = "https://errors.enterprisecommerce.example/catalog/brand-not-found" }, "application/problem+json"),
            "500" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            "502" => new HttpResponseMessage(HttpStatusCode.BadGateway),
            "503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "401" => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            "302" => new HttpResponseMessage(HttpStatusCode.Redirect),
            "malformed JSON" => Raw("{\"id\": "),
            "an array" => Raw("[]"),
            "another product" => Json(HttpStatusCode.OK, Product(id: Guid.CreateVersion7())),
            "no tenant" => Json(HttpStatusCode.OK, Without(Product(), "tenantId")),
            "an empty tenant" => Json(HttpStatusCode.OK, With(Product(), "tenantId", Guid.Empty.ToString())),
            "no name" => Json(HttpStatusCode.OK, Without(Product(), "name")),
            "no variants" => Json(HttpStatusCode.OK, Without(Product(), "variants")),
            "no media" => Json(HttpStatusCode.OK, Without(Product(), "media")),
            "a variant without price" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(Variant(VariantM, noPrice: true)))),
            "a string amount" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(Variant(VariantM, price: new JsonObject { ["amount"] = "19.99", ["currency"] = "EUR" })))),
            "three decimals" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(Variant(VariantM, price: Price(19.999m))))),
            "a zero price" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(Variant(VariantM, price: Price(0m))))),
            "a lower-case currency" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(Variant(VariantM, price: Price(1m, "eur"))))),
            "a variant ID twice" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(Variant(VariantM), Variant(VariantM)))),
            "an http image" => Json(HttpStatusCode.OK, Product(media: new JsonArray(new JsonObject { ["url"] = "http://cdn.example.com/a.jpg" }))),
            "a long name" => Json(HttpStatusCode.OK, With(Product(), "name", new string('n', 201))),
            "too many options" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(Variant(VariantM, options: new JsonArray([.. Enumerable.Range(0, 11).Select(i => (JsonNode)new JsonObject { ["key"] = $"k{i}", ["value"] = "v" })]))))),
            "an option without value" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(Variant(VariantM, options: new JsonArray(new JsonObject { ["key"] = "size" }))))),
            "a non-boolean isActive" => Json(HttpStatusCode.OK, Product(variants: new JsonArray(With(Variant(VariantM), "isActive", "true")))),
            _ => throw new ArgumentOutOfRangeException(nameof(answer)),
        });

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => Reader(handler).ReadAsync(ProductId, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATransportError_IsAnOutage()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => Reader(handler).ReadAsync(ProductId, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATimeout_IsAnOutage_WithinTheBound()
    {
        var handler = new StubHandler(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Json(HttpStatusCode.OK, Product());
        });
        var started = DateTimeOffset.UtcNow;

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => Reader(handler, timeout: TimeSpan.FromMilliseconds(200)).ReadAsync(ProductId, null, TestContext.Current.CancellationToken));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AnOversizedAnswer_IsAnOutage()
    {
        var product = Product();
        product["description"] = new string('d', 300 * 1024);

        await Assert.ThrowsAsync<CatalogUnavailableException>(() =>
            Reader(new StubHandler(_ => Json(HttpStatusCode.OK, product)), maxResponseBytes: 64 * 1024).ReadAsync(ProductId, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheCallersCancellation_IsCancellation_NotAnOutage()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new StubHandler(async (_, token) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return Json(HttpStatusCode.OK, Product());
        });

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(handler).ReadAsync(ProductId, null, cancellation.Token));

        Assert.IsNotType<CatalogUnavailableException>(error);
    }

    [Fact]
    public async Task WithoutAnAddress_EveryReadIsAnOutage()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Product()));
        var reader = new CatalogProductReader(new HttpClient(handler), Options.Create(new CatalogReadOptions()), NullLogger<CatalogProductReader>.Instance);

        await Assert.ThrowsAsync<CatalogUnavailableException>(() => reader.ReadAsync(ProductId, null, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void TheAddress_IsRequiredAndHttpsOutsideDevelopment()
    {
        Assert.NotEmpty(new CatalogReadOptions().Validate(isDevelopment: false));
        Assert.Empty(new CatalogReadOptions().Validate(isDevelopment: true));
        Assert.NotEmpty(new CatalogReadOptions { BaseAddress = new Uri("http://catalog.internal/") }.Validate(isDevelopment: false));
        Assert.NotEmpty(new CatalogReadOptions { BaseAddress = new Uri("https://catalog.internal/api") }.Validate(isDevelopment: false));
        Assert.Empty(new CatalogReadOptions { BaseAddress = new Uri("https://catalog.internal/") }.Validate(isDevelopment: false));
        Assert.NotEmpty(new CatalogReadOptions { BaseAddress = Catalog, Timeout = TimeSpan.FromSeconds(11) }.Validate(isDevelopment: false));
        Assert.NotEmpty(new CatalogReadOptions { BaseAddress = Catalog, RefreshParallelism = 0 }.Validate(isDevelopment: false));
    }

    private static CatalogProductReader Reader(StubHandler handler, TimeSpan? timeout = null, int maxResponseBytes = 1024 * 1024) =>
        new(
            new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = maxResponseBytes },
            Options.Create(new CatalogReadOptions { BaseAddress = Catalog }),
            NullLogger<CatalogProductReader>.Instance);

    /// <summary>A public product as CATALOG_API_CONTRACT.md section 2 shows it, with one active and one inactive variant.</summary>
    internal static JsonObject Product(Guid? id = null, JsonArray? variants = null, JsonArray? media = null) => new()
    {
        ["id"] = (id ?? ProductId).ToString(),
        ["tenantId"] = TenantId.ToString(),
        ["slug"] = "blue-shirt",
        ["name"] = "Blue shirt",
        ["shortDescription"] = "A shirt",
        ["description"] = "A blue shirt.",
        ["brand"] = null,
        ["categories"] = new JsonArray(),
        ["attributes"] = new JsonArray(new JsonObject { ["key"] = "material", ["value"] = "Cotton" }),
        ["variants"] = variants ?? new JsonArray(Variant(VariantM), Variant(VariantL, sku: "BS-L", active: false)),
        ["media"] = media ?? new JsonArray(new JsonObject { ["id"] = Guid.CreateVersion7().ToString(), ["url"] = "https://cdn.example.com/a.jpg", ["altText"] = "Front", ["contentType"] = "image/jpeg" }),
        ["publishedAt"] = "2026-11-23T10:00:00+00:00",
    };

    private static JsonObject Variant(Guid id, string sku = "BS-M", bool active = true, JsonNode? price = null, JsonArray? options = null, bool noPrice = false) => new()
    {
        ["id"] = id.ToString(),
        ["sku"] = sku,
        ["options"] = options ?? new JsonArray(new JsonObject { ["key"] = "size", ["value"] = "M" }),
        ["price"] = noPrice ? null : price ?? Price(19.99m),
        ["isActive"] = active,
    };

    private static JsonObject Price(decimal amount, string currency = "EUR") => new() { ["amount"] = amount, ["currency"] = currency };

    private static JsonObject Without(JsonObject node, string member)
    {
        node.Remove(member);
        return node;
    }

    private static JsonObject With(JsonObject node, string member, JsonNode? value)
    {
        node[member] = value;
        return node;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body, string mediaType = "application/json") =>
        new(status) { Content = new StringContent(body.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, mediaType) };

    private static HttpResponseMessage Raw(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = (request, _) => Task.FromResult(answer(request));

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) => _answer = answer;

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _answer(request, cancellationToken);
        }
    }
}
