using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// Public discovery end to end on MongoDB (CATALOG_API_CONTRACT.md section 2, ADR-024 section 9): published products
/// only, the public view, every filter, sorting and bounded pagination. Each test filters by its own tenant, because
/// the tests of the class share one database.
/// </summary>
public sealed class PublicDiscoveryTests(CatalogMongoFixture fixture) : IClassFixture<CatalogMongoFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DraftAndArchivedProducts_AreNeverPublic()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var category = await fixture.SeedCategoryAsync();
        var draft = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("hidden-draft", [category.Id]));
        var archived = await PublishAsync(tenantId, owner, CatalogApi.ProductBody("hidden-archived", [category.Id]));
        await CommandAsync(tenantId, owner, archived, "archive");
        var published = await PublishAsync(tenantId, owner, CatalogApi.ProductBody("visible", [category.Id]));

        Assert.Equal(["visible"], Slugs(await ListAsync($"tenantId={tenantId}")));
        foreach (var hidden in new[] { draft.GetProperty("id").GetGuid(), archived })
        {
            using var read = await Anonymous().GetAsync($"/api/v1/catalog/products/{hidden}", Token);
            await CatalogApi.AssertProblemAsync(read, HttpStatusCode.NotFound, "product-not-found");
        }

        using var lookup = await Anonymous().GetAsync($"/api/v1/catalog/products/lookup?tenantId={tenantId}&slug=hidden-draft", Token);
        await CatalogApi.AssertProblemAsync(lookup, HttpStatusCode.NotFound, "product-not-found");
        using var visible = await Anonymous().GetAsync($"/api/v1/catalog/products/lookup?tenantId={tenantId}&slug=visible", Token);
        Assert.Equal(published, (await CatalogApi.ReadAsync(visible)).GetProperty("id").GetGuid());

        await CommandAsync(tenantId, owner, published, "unpublish");
        Assert.Empty(Slugs(await ListAsync($"tenantId={tenantId}")));
    }

    [Fact]
    public async Task ThePublicView_ShowsOnlyActiveParts_AndNoInternalFields()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var kept = await fixture.SeedCategoryAsync();
        var toRetire = await fixture.SeedCategoryAsync();
        var brandId = await CreateBrandAsync(tenantId, owner, "public-brand");
        var body = new
        {
            slug = "public-view",
            name = "Public view",
            brandId,
            categoryIds = new[] { kept.Id, toRetire.Id },
            variants = new object[]
            {
                new { sku = "PV-ON", options = new[] { new { key = "size", value = "M" } }, price = new { amount = 10m, currency = "EUR" }, isActive = true },
                new { sku = "PV-OFF", options = new[] { new { key = "size", value = "L" } }, price = new { amount = 12m, currency = "EUR" }, isActive = false },
            },
        };
        var productId = await PublishAsync(tenantId, owner, body);

        var product = await GetAsync(productId);
        Assert.Equal(["PV-ON"], product.GetProperty("variants").EnumerateArray().Select(variant => variant.GetProperty("sku").GetString()));
        Assert.Equal("public-brand", product.GetProperty("brand").GetProperty("slug").GetString());
        Assert.Equal(2, product.GetProperty("categories").GetArrayLength());
        foreach (var internalField in new[] { "status", "version", "createdBy", "updatedBy", "creation", "archivedAt" })
        {
            Assert.False(product.TryGetProperty(internalField, out _), internalField);
        }

        await RetireCategoryDirectlyAsync(toRetire.Id);
        using (var retire = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/brands/{brandId}/retire"))
        {
            Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        }

        product = await GetAsync(productId);
        Assert.Equal(JsonValueKind.Null, product.GetProperty("brand").ValueKind);
        Assert.Equal([kept.Id], product.GetProperty("categories").EnumerateArray().Select(category => category.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task CategoryFilters_IncludeDescendants_AndBrandFiltersMatchExactly()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var parent = await fixture.SeedCategoryAsync();
        var child = await fixture.SeedCategoryAsync(parent);
        var other = await fixture.SeedCategoryAsync();
        var brandId = await CreateBrandAsync(tenantId, owner, "filter-brand");
        await PublishAsync(tenantId, owner, CatalogApi.ProductBody("in-child", [child.Id], brandId: brandId));
        await PublishAsync(tenantId, owner, CatalogApi.ProductBody("in-other", [other.Id]));

        Assert.Equal(["in-child"], Slugs(await ListAsync($"tenantId={tenantId}&categoryId={parent.Id}")));
        Assert.Equal(["in-child"], Slugs(await ListAsync($"tenantId={tenantId}&categoryId={child.Id}")));
        Assert.Equal(["in-child"], Slugs(await ListAsync($"tenantId={tenantId}&brandId={brandId}")));
        Assert.Equal(["in-other"], Slugs(await ListAsync($"tenantId={tenantId}&categoryId={other.Id}")));
    }

    [Fact]
    public async Task AttributeFilters_MatchWithinOneAttribute_WithoutRegardToCase()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var category = await fixture.SeedCategoryAsync();
        await PublishAsync(tenantId, owner, WithAttributes("red-matte", category.Id, ("color", "Red"), ("finish", "Matte")));
        await PublishAsync(tenantId, owner, WithAttributes("matte-gloss", category.Id, ("color", "Matte"), ("finish", "Gloss")));

        Assert.Equal(["red-matte"], Slugs(await ListAsync($"tenantId={tenantId}&attr=color:red")));
        Assert.Equal(["red-matte"], Slugs(await ListAsync($"tenantId={tenantId}&attr=color:RED&attr=finish:matte")));
        Assert.Equal(["matte-gloss"], Slugs(await ListAsync($"tenantId={tenantId}&attr=color:matte")));
        Assert.Empty(Slugs(await ListAsync($"tenantId={tenantId}&attr=color:red&attr=finish:gloss")));
    }

    [Fact]
    public async Task TextSearch_MatchesWholeWords()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var category = await fixture.SeedCategoryAsync();
        await PublishAsync(tenantId, owner, CatalogApi.ProductBody("teapot", [category.Id], name: "Porcelain teapot"));
        await PublishAsync(tenantId, owner, CatalogApi.ProductBody("mug", [category.Id], name: "Stoneware mug"));

        Assert.Equal(["teapot"], Slugs(await ListAsync($"tenantId={tenantId}&q=teapot")));
        Assert.Equal(["mug"], Slugs(await ListAsync($"tenantId={tenantId}&q=STONEWARE")));
        Assert.Empty(Slugs(await ListAsync($"tenantId={tenantId}&q=teap")));
        Assert.Equal(["teapot"], Slugs(await ListAsync($"tenantId={tenantId}&q=-teapot")));
    }

    [Fact]
    public async Task Sorting_IsByPublicationTime_AndPagingIsStable()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var category = await fixture.SeedCategoryAsync();
        for (var i = 0; i < 4; i++)
        {
            var id = await PublishAsync(tenantId, owner, CatalogApi.ProductBody($"sorted-{i}", [category.Id]));
            await CatalogApi.PassAsync((await GetAsync(id)).GetProperty("publishedAt").GetDateTimeOffset());
        }

        Assert.Equal(["sorted-3", "sorted-2", "sorted-1", "sorted-0"], Slugs(await ListAsync($"tenantId={tenantId}")));
        Assert.Equal(["sorted-0", "sorted-1", "sorted-2", "sorted-3"], Slugs(await ListAsync($"tenantId={tenantId}&sort=oldest")));
        var second = await ListAsync($"tenantId={tenantId}&sort=oldest&page=2&pageSize=3");
        Assert.Equal(["sorted-3"], Slugs(second));
        Assert.Equal(4, second.GetProperty("totalCount").GetInt64());
    }

    [Theory]
    [InlineData("page=101")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("sort=price")]
    [InlineData("categoryId=not-a-guid")]
    [InlineData("tenantId=00000000-0000-0000-0000-000000000000")]
    [InlineData("q=x")]
    [InlineData("attr=color")]
    [InlineData("attr=Color:red")]
    [InlineData("attr=a:1&attr=b:2&attr=c:3&attr=d:4&attr=e:5&attr=f:6")]
    public async Task InvalidQueries_Are400(string query)
    {
        using var response = await Anonymous().GetAsync($"/api/v1/catalog/products?{query}", Token);

        await CatalogApi.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Theory]
    [InlineData("tenantId=00000000-0000-7000-8000-000000000001")]
    [InlineData("slug=some-product")]
    [InlineData("tenantId=00000000-0000-7000-8000-000000000001&slug=Bad")]
    public async Task InvalidLookups_Are400(string query)
    {
        using var response = await Anonymous().GetAsync($"/api/v1/catalog/products/lookup?{query}", Token);

        await CatalogApi.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task Discovery_IsAnonymous_AndIgnoresAnyToken()
    {
        fixture.RequireAvailable();
        using var anonymous = await Anonymous().GetAsync("/api/v1/catalog/products", Token);
        using var garbage = await fixture.CreateClient("garbage").GetAsync("/api/v1/catalog/products", Token);
        using var storefront = await fixture.CreateClient(fixture.Tokens.CreateToken(TokenSpec.User("shopper") with { Audiences = ["ec-storefront"] }))
            .GetAsync("/api/v1/catalog/products", Token);

        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.OK, storefront.StatusCode);
    }

    private static object WithAttributes(string slug, Guid categoryId, params (string Key, string Value)[] attributes) => new
    {
        slug,
        name = slug,
        categoryIds = new[] { categoryId },
        attributes = attributes.Select(attribute => new { key = attribute.Key, value = attribute.Value }).ToArray(),
        variants = new[] { new { sku = $"SKU-{slug}", price = new { amount = 5m, currency = "EUR" } } },
    };

    private static string?[] Slugs(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("slug").GetString())];

    private HttpClient Anonymous() => fixture.CreateClient();

    private async Task<JsonElement> ListAsync(string query)
    {
        using var response = await Anonymous().GetAsync($"/api/v1/catalog/products?{query}", Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return await CatalogApi.ReadAsync(response);
    }

    private async Task<JsonElement> GetAsync(Guid productId)
    {
        using var response = await Anonymous().GetAsync($"/api/v1/catalog/products/{productId}", Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return await CatalogApi.ReadAsync(response);
    }

    private async Task<Guid> PublishAsync(Guid tenantId, string owner, object body)
    {
        var product = await fixture.CreateProductAsync(tenantId, owner, body);
        var id = product.GetProperty("id").GetGuid();
        await CommandAsync(tenantId, owner, id, "publish");
        return id;
    }

    private async Task CommandAsync(Guid tenantId, string owner, Guid productId, string command)
    {
        using var response = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products/{productId}/{command}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
    }

    private async Task<Guid> CreateBrandAsync(Guid tenantId, string owner, string slug)
    {
        using var response = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/brands", new { slug, name = slug });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await CatalogApi.ReadAsync(response)).GetProperty("id").GetGuid();
    }

    private async Task RetireCategoryDirectlyAsync(Guid categoryId)
    {
        using var response = await fixture.CreateClient(fixture.TaxonomyToken()).PostAsync($"/internal/v1/catalog/categories/{categoryId}/retire", null, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
