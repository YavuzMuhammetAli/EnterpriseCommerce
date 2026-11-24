using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// The internal variant verification for other services (ADR-027 section 1, CATALOG_API_CONTRACT.md section 6a): the
/// verdicts, tenant isolation, and the allow-listed service caller only.
/// </summary>
public sealed class VariantVerificationApiTests(CatalogMongoFixture fixture) : IClassFixture<CatalogMongoFixture>
{
    private static readonly TokenSpec Verifier = new()
    {
        Subject = TestTokenIssuer.VariantVerificationClient,
        ClientId = TestTokenIssuer.VariantVerificationClient,
        Scope = TestTokenIssuer.VariantVerificationScope,
    };

    public static TheoryData<string, TokenSpec> TokensRejectedByThePolicy() => new()
    {
        { "a merchant user of the tenant", TokenSpec.User("owner-x") },
        { "a client that is not allow-listed", Verifier with { Subject = "svc-rogue", ClientId = "svc-rogue" } },
        { "the allow-listed taxonomy client", new TokenSpec { Scope = TestTokenIssuer.VariantVerificationScope } },
        { "the allow-listed client without the scope", Verifier with { Scope = TestTokenIssuer.TaxonomyScope } },
        { "sub differs from client_id", Verifier with { Subject = "someone" } },
        { "a user principal type", Verifier with { PrincipalType = "user" } },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AVariantOfTheTenantsProduct_IsFound_WithItsStatusSellabilityAndSku()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var product = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody(Slug(), sku: "VERIFY-1"));

        var verdict = await VerifyAsync(tenantId, product.GetProperty("id").GetGuid(), VariantOf(product));

        Assert.Equal((true, "Draft", true, "VERIFY-1"), (verdict.GetProperty("found").GetBoolean(), verdict.GetProperty("productStatus").GetString(), verdict.GetProperty("variantActive").GetBoolean(), verdict.GetProperty("sku").GetString()));
    }

    [Fact]
    public async Task AnArchivedProduct_IsReportedAsArchived()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var product = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody(Slug()));
        var productId = product.GetProperty("id").GetGuid();
        using (var archived = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products/{productId}/archive"))
        {
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        }

        var verdict = await VerifyAsync(tenantId, productId, VariantOf(product));

        Assert.Equal((true, "Archived"), (verdict.GetProperty("found").GetBoolean(), verdict.GetProperty("productStatus").GetString()));
    }

    [Fact]
    public async Task AnotherTenantsProduct_AnUnknownProduct_AndAVariantOfAnotherProduct_AreAllNotFound()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var otherTenant = fixture.NewTenant(out var otherOwner, out _);
        var product = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody(Slug()));
        var sibling = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody(Slug()));
        var foreign = await fixture.CreateProductAsync(otherTenant, otherOwner, CatalogApi.ProductBody(Slug()));
        var productId = product.GetProperty("id").GetGuid();

        foreach (var (tenant, productRef, variant) in new[]
        {
            (otherTenant, productId, VariantOf(product)),
            (tenantId, foreign.GetProperty("id").GetGuid(), VariantOf(foreign)),
            (tenantId, Guid.CreateVersion7(), VariantOf(product)),
            (tenantId, productId, VariantOf(sibling)),
            (tenantId, productId, Guid.CreateVersion7()),
        })
        {
            var verdict = await VerifyAsync(tenant, productRef, variant);
            Assert.Equal(
                "{\"found\":false,\"productStatus\":null,\"variantActive\":null,\"sku\":null}",
                verdict.GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(TokensRejectedByThePolicy))]
    public async Task AnyOtherCaller_IsForbidden(string caller, TokenSpec spec)
    {
        _ = caller;
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(spec));

        using var response = await client.PostAsJsonAsync(Path(Guid.CreateVersion7()), new { productId = Guid.CreateVersion7(), variantId = Guid.CreateVersion7() }, Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("no token")]
    [InlineData("a token for another service's audience")]
    public async Task WithoutAValidCatalogToken_TheCallerIsUnauthorized(string caller)
    {
        // The audience is part of token validation (ADR-017 section 4): another service's token is never authenticated.
        using var client = fixture.CreateClient(caller == "no token" ? null : fixture.Tokens.CreateToken(Verifier with { Audiences = ["inventory-api"] }));

        using var response = await client.PostAsJsonAsync(Path(Guid.CreateVersion7()), new { productId = Guid.CreateVersion7(), variantId = Guid.CreateVersion7() }, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"productId":"00000000-0000-0000-0000-000000000000","variantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d"}""")]
    [InlineData("""{"productId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d"}""")]
    [InlineData("""{"productId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","variantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","tenantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d"}""")]
    public async Task AMalformedRequest_IsRefused(string body)
    {
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(Verifier));
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(Path(Guid.CreateVersion7()), content, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<JsonElement> VerifyAsync(Guid tenantId, Guid productId, Guid variantId)
    {
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(Verifier));
        using var response = await client.PostAsJsonAsync(Path(tenantId), new { productId, variantId }, Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return await CatalogApi.ReadAsync(response);
    }

    private static Guid VariantOf(JsonElement product) => product.GetProperty("variants")[0].GetProperty("id").GetGuid();

    private static string Path(Guid tenantId) => $"/internal/v1/catalog/tenants/{tenantId}/variant-verifications";

    private static string Slug() => $"p-{Guid.NewGuid():N}";
}
