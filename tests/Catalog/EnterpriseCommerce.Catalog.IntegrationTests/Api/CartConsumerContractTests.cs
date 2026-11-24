using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// The parts of the public product read that Cart depends on (CATALOG_API_CONTRACT.md section 2.2, ADR-029 section 5).
/// Cart snapshots a product from exactly these fields and treats only this <c>404</c> problem type as "not
/// published"; a change that breaks one of these assertions breaks Cart, and needs a contract change first.
/// </summary>
public sealed class CartConsumerContractTests(CatalogMongoFixture fixture) : IClassFixture<CatalogMongoFixture>
{
    private const string ProductNotFoundType = "https://errors.enterprisecommerce.example/catalog/product-not-found";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APublishedProduct_CarriesEveryFieldCartSnapshots_WithTheTypesCartExpects()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var category = await fixture.SeedCategoryAsync();
        var created = await fixture.CreateProductAsync(tenantId, owner, new
        {
            slug = "cart-contract",
            name = "Cart contract shirt",
            categoryIds = new[] { category.Id },
            variants = new object[]
            {
                new { sku = "CC-M", options = new[] { new { key = "size", value = "M" } }, price = new { amount = 19.99m, currency = "EUR" }, isActive = true },
                new { sku = "CC-L", options = new[] { new { key = "size", value = "L" } }, price = new { amount = 21m, currency = "EUR" }, isActive = false },
            },
        });
        var productId = created.GetProperty("id").GetGuid();
        using (var media = await fixture.SendAsync(
            owner,
            HttpMethod.Put,
            $"/api/v1/catalog/tenants/{tenantId}/products/{productId}/media",
            new { media = new[] { new { url = "https://cdn.example.com/front.jpg", altText = "Front", contentType = "image/jpeg" } } },
            CatalogApi.IfMatch(created.GetProperty("version").GetInt64())))
        {
            Assert.True(media.StatusCode == HttpStatusCode.OK, await media.Content.ReadAsStringAsync(Token));
        }

        using (var publish = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products/{productId}/publish"))
        {
            Assert.True(publish.StatusCode == HttpStatusCode.OK, await publish.Content.ReadAsStringAsync(Token));
        }

        // Anonymously, as Cart calls it: no token at all.
        using var response = await fixture.CreateClient().GetAsync($"/api/v1/catalog/products/{productId:D}", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var product = await CatalogApi.ReadAsync(response);

        Assert.Equal(productId.ToString("D"), product.GetProperty("id").GetString());
        Assert.Equal(tenantId.ToString("D"), product.GetProperty("tenantId").GetString());
        Assert.Equal("Cart contract shirt", product.GetProperty("name").GetString());

        var variant = Assert.Single(product.GetProperty("variants").EnumerateArray());
        Assert.True(Guid.TryParseExact(variant.GetProperty("id").GetString(), "D", out _));
        Assert.Equal("CC-M", variant.GetProperty("sku").GetString());
        Assert.Equal(JsonValueKind.True, variant.GetProperty("isActive").ValueKind);
        var option = Assert.Single(variant.GetProperty("options").EnumerateArray());
        Assert.Equal("size", option.GetProperty("key").GetString());
        Assert.Equal("M", option.GetProperty("value").GetString());
        var price = variant.GetProperty("price");
        Assert.Equal(JsonValueKind.Number, price.GetProperty("amount").ValueKind);
        Assert.Equal(19.99m, price.GetProperty("amount").GetDecimal());
        Assert.Equal("EUR", price.GetProperty("currency").GetString());

        var image = Assert.Single(product.GetProperty("media").EnumerateArray());
        Assert.Equal("https://cdn.example.com/front.jpg", image.GetProperty("url").GetString());
        Assert.Equal("Front", image.GetProperty("altText").GetString());
    }

    [Fact]
    public async Task AnUnpublishedOrUnknownProduct_AnswersTheExactProblemTypeCartRecognizes()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var draft = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("cart-contract-draft"));

        foreach (var productId in new[] { draft.GetProperty("id").GetGuid(), Guid.CreateVersion7() })
        {
            using var response = await fixture.CreateClient().GetAsync($"/api/v1/catalog/products/{productId:D}", Token);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(ProductNotFoundType, (await CatalogApi.ReadAsync(response)).GetProperty("type").GetString());
        }
    }
}
