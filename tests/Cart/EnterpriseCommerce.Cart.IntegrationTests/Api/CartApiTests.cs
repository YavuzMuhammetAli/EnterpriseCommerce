using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Cart.IntegrationTests.Api;

/// <summary>The customer cart API end to end on SQL Server (CART_API_CONTRACT.md sections 3 to 8).</summary>
public sealed class CartApiTests(CartApiFixture fixture) : IClassFixture<CartApiFixture>
{
    private const string Items = "/api/v1/cart/items";

    [Fact]
    public async Task ACustomerWithoutACart_ReadsAnEmptyCart_WithVersionZero_AndNoRowIsCreated()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();

        using var response = await fixture.SendAsync(customer, HttpMethod.Get, "/api/v1/cart");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"0\"", response.Headers.ETag?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var cart = await CartApiFixture.ReadAsync(response);
        Assert.Equal(JsonValueKind.Null, cart.GetProperty("id").ValueKind);
        Assert.Equal(0, cart.GetProperty("version").GetInt32());
        Assert.Equal(0, cart.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, cart.GetProperty("subtotal").ValueKind);
        Assert.Equal(JsonValueKind.Null, cart.GetProperty("pricing").ValueKind);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject = N'{customer}'"));
    }

    [Fact]
    public async Task AddingAVariant_CreatesTheCart_WithTheCatalogSnapshot()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var variant = StubVariant.New(amount: 19.99m);
        var product = fixture.Catalog.Publish(merchant, variant);

        using var response = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = product, variantId = variant.Id, quantity = 2 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag?.ToString());
        var cart = await CartApiFixture.ReadAsync(response);
        var line = Assert.Single(cart.GetProperty("items").EnumerateArray());
        Assert.Equal($"/api/v1/cart/items/{line.GetProperty("id").GetGuid():D}", response.Headers.Location?.OriginalString);
        Assert.Equal(merchant, cart.GetProperty("merchantTenantId").GetGuid());
        Assert.Equal("EUR", cart.GetProperty("currency").GetString());
        Assert.Equal(product, line.GetProperty("productId").GetGuid());
        Assert.Equal(variant.Id, line.GetProperty("variantId").GetGuid());
        Assert.Equal("Blue shirt", line.GetProperty("name").GetString());
        Assert.Equal("BS-M", line.GetProperty("sku").GetString());
        Assert.Equal("M", line.GetProperty("options")[0].GetProperty("value").GetString());
        Assert.Equal("https://cdn.example.com/a.jpg", line.GetProperty("image").GetProperty("url").GetString());
        Assert.Equal(2, line.GetProperty("quantity").GetInt32());
        Assert.Equal(19.99m, line.GetProperty("unitPrice").GetProperty("amount").GetDecimal());
        Assert.Equal(39.98m, line.GetProperty("lineTotal").GetProperty("amount").GetDecimal());
        Assert.Equal("available", line.GetProperty("availability").GetString());
        Assert.Equal(39.98m, cart.GetProperty("subtotal").GetProperty("amount").GetDecimal());
        Assert.Equal(1, cart.GetProperty("lineCount").GetInt32());
        Assert.Equal(2, cart.GetProperty("totalQuantity").GetInt32());
        Assert.False(cart.GetProperty("pricing").GetProperty("stale").GetBoolean());
        Assert.Equal(cart.GetProperty("updatedAt").GetDateTimeOffset().AddDays(30), cart.GetProperty("expiresAt").GetDateTimeOffset());

        Assert.Equal(cart.GetRawText(), (await fixture.GetCartAsync(customer)).GetRawText());
    }

    [Fact]
    public async Task AddingTheSameVariantAgain_AddsToItsLine_AndTakesTheCurrentPrice()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var variant = StubVariant.New(amount: 10m);
        var product = fixture.Catalog.Publish(merchant, variant);
        await fixture.AddAsync(customer, product, variant.Id, 2);
        fixture.Catalog.Set(product, merchant, variant with { Amount = 9m });

        using var response = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = product, variantId = variant.Id, quantity = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single((await CartApiFixture.ReadAsync(response)).GetProperty("items").EnumerateArray());
        Assert.Equal(5, line.GetProperty("quantity").GetInt32());
        Assert.Equal(9m, line.GetProperty("unitPrice").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task AQuantity_IsSet_AnItemRemoved_AndTheCartCleared()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var first = StubVariant.New();
        var second = StubVariant.New();
        var product = fixture.Catalog.Publish(merchant, first, second);
        await fixture.AddAsync(customer, product, first.Id);
        var cart = await fixture.AddAsync(customer, product, second.Id);
        var firstLine = cart.GetProperty("items")[0].GetProperty("id").GetGuid();

        using (var set = await fixture.SendAsync(customer, HttpMethod.Put, $"{Items}/{firstLine}", new { quantity = 7 }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal(7, (await CartApiFixture.ReadAsync(set)).GetProperty("items")[0].GetProperty("quantity").GetInt32());
        }

        using (var same = await fixture.SendAsync(customer, HttpMethod.Put, $"{Items}/{firstLine}", new { quantity = 7 }))
        {
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
            Assert.Equal("\"3\"", same.Headers.ETag?.ToString());
        }

        using (var remove = await fixture.SendAsync(customer, HttpMethod.Delete, $"{Items}/{firstLine}"))
        {
            Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
            Assert.Equal(second.Id, Assert.Single((await CartApiFixture.ReadAsync(remove)).GetProperty("items").EnumerateArray()).GetProperty("variantId").GetGuid());
        }

        using (var clear = await fixture.SendAsync(customer, HttpMethod.Delete, Items))
        {
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            var cleared = await CartApiFixture.ReadAsync(clear);
            Assert.Equal(0, cleared.GetProperty("items").GetArrayLength());
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("merchantTenantId").ValueKind);
            Assert.Equal(5, cleared.GetProperty("version").GetInt32());
        }

        using var again = await fixture.SendAsync(customer, HttpMethod.Delete, Items);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("\"5\"", again.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task ClearingWithoutACart_AnswersAnEmptyCart()
    {
        fixture.RequireAvailable();

        using var response = await fixture.SendAsync(CartApiFixture.NewCustomer(), HttpMethod.Delete, Items);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await CartApiFixture.ReadAsync(response)).GetProperty("id").ValueKind);
    }

    [Fact]
    public async Task AnUnknownLine_Is404()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id);

        using var set = await fixture.SendAsync(customer, HttpMethod.Put, $"{Items}/{Guid.CreateVersion7()}", new { quantity = 1 });
        using var remove = await fixture.SendAsync(customer, HttpMethod.Delete, $"{Items}/{Guid.CreateVersion7()}");
        using var withoutCart = await fixture.SendAsync(CartApiFixture.NewCustomer(), HttpMethod.Delete, $"{Items}/{Guid.CreateVersion7()}");

        await CartApiFixture.AssertProblemAsync(set, HttpStatusCode.NotFound, "cart-item-not-found");
        await CartApiFixture.AssertProblemAsync(remove, HttpStatusCode.NotFound, "cart-item-not-found");
        await CartApiFixture.AssertProblemAsync(withoutCart, HttpStatusCode.NotFound, "cart-item-not-found");
    }

    [Fact]
    public async Task AnotherMerchantsProduct_Is409_AndTheCartIsUntouched()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchantA = Guid.CreateVersion7();
        var first = StubVariant.New();
        await fixture.AddAsync(customer, fixture.Catalog.Publish(merchantA, first), first.Id);
        var other = StubVariant.New();
        var otherProduct = fixture.Catalog.Publish(Guid.CreateVersion7(), other);

        using var response = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = otherProduct, variantId = other.Id, quantity = 1 });

        var problem = await CartApiFixture.AssertProblemAsync(response, HttpStatusCode.Conflict, "merchant-mismatch");
        Assert.Equal(merchantA, problem.GetProperty("cartMerchantTenantId").GetGuid());
        var cart = await fixture.GetCartAsync(customer);
        Assert.Equal(first.Id, Assert.Single(cart.GetProperty("items").EnumerateArray()).GetProperty("variantId").GetGuid());
        Assert.Equal(1, cart.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task AnotherCurrency_Is409()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var eur = StubVariant.New(currency: "EUR");
        await fixture.AddAsync(customer, fixture.Catalog.Publish(merchant, eur), eur.Id);
        var usd = StubVariant.New(currency: "USD");

        using var response = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = fixture.Catalog.Publish(merchant, usd), variantId = usd.Id, quantity = 1 });

        var problem = await CartApiFixture.AssertProblemAsync(response, HttpStatusCode.Conflict, "currency-mismatch");
        Assert.Equal("EUR", problem.GetProperty("cartCurrency").GetString());
    }

    [Fact]
    public async Task TheLimits_AreEnforced()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var variants = Enumerable.Range(0, 51).Select(_ => StubVariant.New()).ToArray();
        var product = fixture.Catalog.Publish(merchant, variants);
        await fixture.AddAsync(customer, product, variants[0].Id, 99);

        using (var tooMany = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = product, variantId = variants[0].Id, quantity = 1 }))
        {
            await CartApiFixture.AssertProblemAsync(tooMany, HttpStatusCode.Conflict, "quantity-limit-exceeded");
        }

        foreach (var variant in variants[1..50])
        {
            await fixture.AddAsync(customer, product, variant.Id);
        }

        using var oneLineTooMany = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = product, variantId = variants[50].Id, quantity = 1 });
        await CartApiFixture.AssertProblemAsync(oneLineTooMany, HttpStatusCode.Conflict, "cart-item-limit-exceeded");
        Assert.Equal(50, (await fixture.GetCartAsync(customer)).GetProperty("lineCount").GetInt32());
    }

    [Fact]
    public async Task AnUnpublishedProduct_OrAMissingOrInactiveVariant_Is422_AndNothingIsWritten()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var active = StubVariant.New();
        var inactive = StubVariant.New(isActive: false);
        var product = fixture.Catalog.Publish(merchant, active, inactive);

        using (var unknownProduct = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = Guid.CreateVersion7(), variantId = active.Id, quantity = 1 }))
        {
            await CartApiFixture.AssertProblemAsync(unknownProduct, HttpStatusCode.UnprocessableEntity, "product-unavailable");
        }

        using (var inactiveVariant = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = product, variantId = inactive.Id, quantity = 1 }))
        {
            await CartApiFixture.AssertProblemAsync(inactiveVariant, HttpStatusCode.UnprocessableEntity, "variant-unavailable");
        }

        using (var otherProductsVariant = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = product, variantId = Guid.CreateVersion7(), quantity = 1 }))
        {
            await CartApiFixture.AssertProblemAsync(otherProductsVariant, HttpStatusCode.UnprocessableEntity, "variant-unavailable");
        }

        fixture.Catalog.Unpublish(product);
        using (var unpublished = await fixture.SendAsync(customer, HttpMethod.Post, Items, new { productId = product, variantId = active.Id, quantity = 1 }))
        {
            await CartApiFixture.AssertProblemAsync(unpublished, HttpStatusCode.UnprocessableEntity, "product-unavailable");
        }

        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject = N'{customer}'"));
    }

    public static TheoryData<string> CatalogOutages() => ["500", "503", "404 without a problem", "garbage", "timeout"];

    [Theory]
    [MemberData(nameof(CatalogOutages))]
    public async Task ACatalogOutage_Is503WithRetryAfter_NeverANotFound_AndNothingIsWritten(string outage)
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
        await using var factory = fixture.CreateFactory(builder => builder.UseSetting("Cart:Catalog:Timeout", "00:00:00.300"));
        switch (outage)
        {
            case "500": fixture.Catalog.FailWith = HttpStatusCode.InternalServerError; break;
            case "503": fixture.Catalog.FailWith = HttpStatusCode.ServiceUnavailable; break;
            case "404 without a problem": fixture.Catalog.FailWith = HttpStatusCode.NotFound; break;
            case "garbage": fixture.Catalog.RawAnswer = "<html>"; break;
            case "timeout": fixture.Catalog.Delay = TimeSpan.FromSeconds(5); break;
            default: throw new ArgumentOutOfRangeException(nameof(outage));
        }

        try
        {
            using var client = CartApiFixture.CreateClient(factory, fixture.CustomerToken(customer));
            using var response = await client.PostAsJsonAsync(Items, new { productId = product, variantId = variant.Id, quantity = 1 }, TestContext.Current.CancellationToken);

            var problem = await CartApiFixture.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "catalog-unavailable");
            Assert.Equal("5", response.Headers.RetryAfter?.ToString());
            Assert.DoesNotContain("Exception", problem.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            fixture.Catalog.FailWith = null;
            fixture.Catalog.RawAnswer = null;
            fixture.Catalog.Delay = TimeSpan.Zero;
        }

        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject = N'{customer}'"));
    }

    [Fact]
    public async Task ReadsAndQuantityChanges_NeverCallCatalog()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var cart = await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id);
        var line = cart.GetProperty("items")[0].GetProperty("id").GetGuid();
        var calls = fixture.Catalog.CallCount;
        fixture.Catalog.FailWith = HttpStatusCode.ServiceUnavailable;
        try
        {
            await fixture.GetCartAsync(customer);
            using var set = await fixture.SendAsync(customer, HttpMethod.Put, $"{Items}/{line}", new { quantity = 3 });
            using var remove = await fixture.SendAsync(customer, HttpMethod.Delete, $"{Items}/{line}");

            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
            Assert.Equal(calls, fixture.Catalog.CallCount);
        }
        finally
        {
            fixture.Catalog.FailWith = null;
        }
    }

    [Fact]
    public async Task ARefresh_ReportsPriceChangesAndUnavailableLines_WithoutRemovingThem()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var cheaper = StubVariant.New(amount: 20m);
        var vanishing = StubVariant.New(amount: 5m);
        var product = fixture.Catalog.Publish(merchant, cheaper);
        var other = fixture.Catalog.Publish(merchant, vanishing);
        await fixture.AddAsync(customer, product, cheaper.Id, 2);
        await fixture.AddAsync(customer, other, vanishing.Id, 1);
        fixture.Catalog.Set(product, merchant, cheaper with { Amount = 17.5m });
        fixture.Catalog.Unpublish(other);

        using var response = await fixture.SendAsync(customer, HttpMethod.Post, "/api/v1/cart/refresh");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await CartApiFixture.ReadAsync(response);
        var changes = body.GetProperty("changes").EnumerateArray().ToList();
        var priceChange = Assert.Single(changes, change => change.GetProperty("kind").GetString() == "priceChanged");
        Assert.Equal(20m, priceChange.GetProperty("previousUnitPrice").GetProperty("amount").GetDecimal());
        Assert.Equal(17.5m, priceChange.GetProperty("unitPrice").GetProperty("amount").GetDecimal());
        var gone = Assert.Single(changes, change => change.GetProperty("kind").GetString() == "becameUnavailable");
        Assert.False(gone.TryGetProperty("unitPrice", out _));

        var cart = body.GetProperty("cart");
        Assert.Equal(2, cart.GetProperty("lineCount").GetInt32());
        Assert.Equal("unavailable", cart.GetProperty("items").EnumerateArray().Single(line => line.GetProperty("variantId").GetGuid() == vanishing.Id).GetProperty("availability").GetString());
        Assert.Equal(35m, cart.GetProperty("subtotal").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task ARefresh_IsAllOrNothing_WhenCatalogFails()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var variant = StubVariant.New(amount: 20m);
        var product = fixture.Catalog.Publish(merchant, variant);
        var before = await fixture.AddAsync(customer, product, variant.Id);
        fixture.Catalog.FailWith = HttpStatusCode.BadGateway;
        try
        {
            using var response = await fixture.SendAsync(customer, HttpMethod.Post, "/api/v1/cart/refresh");
            await CartApiFixture.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "catalog-unavailable");
        }
        finally
        {
            fixture.Catalog.FailWith = null;
        }

        Assert.Equal(before.GetProperty("version").GetInt32(), (await fixture.GetCartAsync(customer)).GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task PricesBecomeStale_AndAnExpiredCartReadsAsEmpty_ThenStartsOver()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(merchant, variant);
        await fixture.AddAsync(customer, product, variant.Id);

        fixture.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.True((await fixture.GetCartAsync(customer)).GetProperty("pricing").GetProperty("stale").GetBoolean());

        fixture.Time.Advance(TimeSpan.FromDays(31));
        var expired = await fixture.GetCartAsync(customer);
        Assert.Equal(JsonValueKind.Null, expired.GetProperty("id").ValueKind);

        var otherMerchant = StubVariant.New(currency: "USD");
        var restarted = await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), otherMerchant), otherMerchant.Id);
        Assert.Equal(otherMerchant.Id, Assert.Single(restarted.GetProperty("items").EnumerateArray()).GetProperty("variantId").GetGuid());
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject = N'{customer}'"));
    }

    [Fact]
    public async Task IfMatch_GuardsEveryMutation()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
        var body = new { productId = product, variantId = variant.Id, quantity = 1 };

        using (var first = await fixture.SendAsync(customer, HttpMethod.Post, Items, body, IfMatch("\"0\"")))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        // The retry of an add that was applied: the tag it was sent with is stale now.
        using (var retry = await fixture.SendAsync(customer, HttpMethod.Post, Items, body, IfMatch("\"0\"")))
        {
            await CartApiFixture.AssertProblemAsync(retry, HttpStatusCode.PreconditionFailed, "precondition-failed");
        }

        foreach (var tag in new[] { "W/\"1\"", "*", "1", "\"x\"", "\"1\", \"2\"" })
        {
            using var malformed = await fixture.SendAsync(customer, HttpMethod.Delete, Items, headers: IfMatch(tag));
            await CartApiFixture.AssertProblemAsync(malformed, HttpStatusCode.PreconditionFailed, "precondition-failed");
        }

        using (var current = await fixture.SendAsync(customer, HttpMethod.Post, Items, body, IfMatch("\"1\"")))
        {
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.Equal("\"2\"", current.Headers.ETag?.ToString());
        }

        Assert.Equal(2, (await fixture.GetCartAsync(customer)).GetProperty("items")[0].GetProperty("quantity").GetInt32());
    }

    public static TheoryData<string> InvalidBodies() =>
    [
        """{"productId":"00000000-0000-0000-0000-000000000000","variantId":"0193b6a2-2222-7333-8444-555566668888","quantity":1}""",
        """{"variantId":"0193b6a2-2222-7333-8444-555566668888","quantity":1}""",
        """{"productId":"0193b6a2-2222-7333-8444-555566667777","variantId":"0193b6a2-2222-7333-8444-555566668888","quantity":0}""",
        """{"productId":"0193b6a2-2222-7333-8444-555566667777","variantId":"0193b6a2-2222-7333-8444-555566668888","quantity":100}""",
        """{"productId":"0193b6a2-2222-7333-8444-555566667777","variantId":"0193b6a2-2222-7333-8444-555566668888"}""",
    ];

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task AnInvalidMember_Is400ValidationFailed(string body)
    {
        fixture.RequireAvailable();
        var calls = fixture.Catalog.CallCount;

        using var response = await fixture.SendAsync(CartApiFixture.NewCustomer(), HttpMethod.Post, Items, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.True((await CartApiFixture.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed")).GetProperty("errors").EnumerateObject().Any());
        Assert.Equal(calls, fixture.Catalog.CallCount);
    }

    public static TheoryData<string> MalformedBodies() =>
    [
        """{"productId":"0193b6a2-2222-7333-8444-555566667777","variantId":"0193b6a2-2222-7333-8444-555566668888","quantity":1.5}""",
        """{"productId":"0193b6a2-2222-7333-8444-555566667777","variantId":"0193b6a2-2222-7333-8444-555566668888","quantity":1,"customerId":"someone-else"}""",
        """{"productId":"0193b6a2-2222-7333-8444-555566667777","variantId":"0193b6a2-2222-7333-8444-555566668888","quantity":1,"unitPrice":{"amount":0.01,"currency":"EUR"}}""",
        """{"productId":"not-a-guid","variantId":"0193b6a2-2222-7333-8444-555566668888","quantity":1}""",
        """{"productId":""",
    ];

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task AMalformedBody_OrAnUndefinedMember_Is400_WithoutParserDetails(string body)
    {
        fixture.RequireAvailable();

        using var response = await fixture.SendAsync(CartApiFixture.NewCustomer(), HttpMethod.Post, Items, new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("https://tools.ietf.org/html/rfc9110#section-15.5.1", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonException", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("customerId", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFractionalOrMissingQuantityChange_Is400()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var cart = await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id);
        var line = cart.GetProperty("items")[0].GetProperty("id").GetGuid();

        using var fractional = await fixture.SendAsync(customer, HttpMethod.Put, $"{Items}/{line}", new StringContent("""{"quantity":2.5}""", Encoding.UTF8, "application/json"));
        using var zero = await fixture.SendAsync(customer, HttpMethod.Put, $"{Items}/{line}", new { quantity = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, fractional.StatusCode);
        await CartApiFixture.AssertProblemAsync(zero, HttpStatusCode.BadRequest, "validation-failed");
    }

    private static Dictionary<string, string> IfMatch(string value) => new() { ["If-Match"] = value };
}
