using System.Net;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Cart.IntegrationTests.Api;

/// <summary>
/// Authentication and ownership (CART_API_CONTRACT.md section 2, ADR-029 section 1): only storefront customer tokens
/// pass, the cart is always the token subject's own, and nothing in a request or a token can name another owner.
/// </summary>
public sealed class CartSecurityTests(CartApiFixture fixture) : IClassFixture<CartApiFixture>
{
    public static TheoryData<string> Endpoints() =>
    [
        "GET /api/v1/cart",
        "POST /api/v1/cart/items",
        "PUT /api/v1/cart/items/0193b6b5-7000-7a00-8b00-000000000001",
        "DELETE /api/v1/cart/items/0193b6b5-7000-7a00-8b00-000000000001",
        "DELETE /api/v1/cart/items",
        "POST /api/v1/cart/refresh",
    ];

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEndpoint_RequiresAToken(string endpoint)
    {
        var (method, path) = Split(endpoint);

        using var response = await fixture.SendWithTokenAsync(null, method, path, method == HttpMethod.Get || method == HttpMethod.Delete ? null : new { quantity = 1 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string> RejectedTokens() =>
    [
        "merchant-portal audience", "admin audience", "service token", "expired", "wrong typ", "unknown key", "other issuer", "HS256", "garbage",
    ];

    [Theory]
    [MemberData(nameof(RejectedTokens))]
    public async Task ATokenThatIsNotAValidStorefrontToken_Is401(string kind)
    {
        var token = kind switch
        {
            "merchant-portal audience" => fixture.Tokens.CreateToken(TokenSpec.Customer("someone") with { Audiences = [TestTokenIssuer.MerchantAudience], ClientId = "bff-merchant" }),
            "admin audience" => fixture.Tokens.CreateToken(TokenSpec.Customer("someone") with { Audiences = ["ec-admin"], ClientId = "bff-admin" }),
            "service token" => fixture.Tokens.CreateToken(new TokenSpec { Subject = "svc-order", ClientId = "svc-order", PrincipalType = "service", Audiences = ["cart-api"], Scope = "svc.cart.read" }),
            "expired" => fixture.Tokens.CreateToken(TokenSpec.Customer("someone") with { ExpiresIn = TimeSpan.FromMinutes(-5) }),
            "wrong typ" => fixture.Tokens.CreateToken(TokenSpec.Customer("someone") with { TokenType = "JWT" }),
            "unknown key" => fixture.Tokens.CreateToken(TokenSpec.Customer("someone") with { SignWithUnknownKey = true }),
            "other issuer" => fixture.Tokens.CreateToken(TokenSpec.Customer("someone") with { Issuer = "https://evil.example/" }),
            "HS256" => "eyJhbGciOiJIUzI1NiIsInR5cCI6ImF0K2p3dCJ9.eyJzdWIiOiJzb21lb25lIiwiYXVkIjoiZWMtc3RvcmVmcm9udCJ9.c2lnbmF0dXJl",
            "garbage" => "not-a-token",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        using var response = await fixture.SendWithTokenAsync(token, HttpMethod.Get, "/api/v1/cart");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string> NotCustomers() => ["service principal", "svc client", "no principal type"];

    [Theory]
    [MemberData(nameof(NotCustomers))]
    public async Task AStorefrontAudienceToken_ThatIsNotAUserToken_Is403(string kind)
    {
        var spec = kind switch
        {
            "service principal" => TokenSpec.Customer("svc-anything") with { PrincipalType = "service", ClientId = "svc-anything" },
            "svc client" => TokenSpec.Customer("someone") with { ClientId = "svc-anything" },
            "no principal type" => TokenSpec.Customer("someone") with { PrincipalType = null },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        using var response = await fixture.SendWithTokenAsync(fixture.Tokens.CreateToken(spec), HttpMethod.Get, "/api/v1/cart");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ACustomer_SeesAndChangesOnlyTheirOwnCart()
    {
        fixture.RequireAvailable();
        var alice = CartApiFixture.NewCustomer();
        var bob = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
        var aliceCart = await fixture.AddAsync(alice, product, variant.Id, 3);
        var aliceLine = aliceCart.GetProperty("items")[0].GetProperty("id").GetGuid();

        Assert.Equal(0, (await fixture.GetCartAsync(bob)).GetProperty("items").GetArrayLength());
        using (var change = await fixture.SendAsync(bob, HttpMethod.Put, $"/api/v1/cart/items/{aliceLine}", new { quantity = 1 }))
        {
            await CartApiFixture.AssertProblemAsync(change, HttpStatusCode.NotFound, "cart-item-not-found");
        }

        using (var remove = await fixture.SendAsync(bob, HttpMethod.Delete, $"/api/v1/cart/items/{aliceLine}"))
        {
            await CartApiFixture.AssertProblemAsync(remove, HttpStatusCode.NotFound, "cart-item-not-found");
        }

        using (var clear = await fixture.SendAsync(bob, HttpMethod.Delete, "/api/v1/cart/items"))
        {
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        }

        var unchanged = await fixture.GetCartAsync(alice);
        Assert.Equal(3, unchanged.GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(aliceCart.GetProperty("version").GetInt32(), unchanged.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task ForgedClaims_AreIgnored_AndTheCartIsTheSubjects()
    {
        fixture.RequireAvailable();
        var victim = CartApiFixture.NewCustomer();
        var attacker = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        await fixture.AddAsync(victim, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id);
        var forged = fixture.Tokens.CreateToken(TokenSpec.Customer(attacker) with
        {
            ExtraClaims = new Dictionary<string, object>
            {
                ["customer_id"] = victim,
                ["owner"] = victim,
                ["role"] = "PlatformAdmin",
                ["tenant_id"] = Guid.CreateVersion7().ToString(),
            },
        });

        using var response = await fixture.SendWithTokenAsync(forged, HttpMethod.Get, "/api/v1/cart", headers: new Dictionary<string, string> { ["X-Customer-Id"] = victim, ["X-User-Id"] = victim });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await CartApiFixture.ReadAsync(response)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task LogsAndCatalogCalls_CarryNoTokenAndNoSubject()
    {
        fixture.RequireAvailable();
        var customer = $"secret-subject-{Guid.NewGuid():N}";
        var token = fixture.CustomerToken(customer);
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);

        using (var add = await fixture.SendWithTokenAsync(token, HttpMethod.Post, "/api/v1/cart/items", new { productId = product, variantId = variant.Id, quantity = 1 }))
        {
            Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        }

        Assert.False(fixture.Catalog.LastHadAuthorization, "Catalog's public read is called anonymously, never with the customer's token.");
        var text = string.Join('\n', fixture.Logs.AllText());
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(token.Split('.')[1], text, StringComparison.Ordinal);
        Assert.DoesNotContain(customer, text, StringComparison.Ordinal);
    }

    private static (HttpMethod Method, string Path) Split(string endpoint)
    {
        var parts = endpoint.Split(' ');
        return (new HttpMethod(parts[0]), parts[1]);
    }
}
