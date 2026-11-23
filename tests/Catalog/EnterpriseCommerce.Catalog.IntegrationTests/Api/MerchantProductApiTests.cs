using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// Merchant product management end to end on MongoDB (CATALOG_API_CONTRACT.md section 4): creation and idempotency,
/// validation, uniqueness, references, optimistic concurrency, the lifecycle and media.
/// </summary>
public sealed class MerchantProductApiTests(CatalogMongoFixture fixture) : IClassFixture<CatalogMongoFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_MakesADraftOfTheRouteTenant_WithAnEntityTag()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);

        using var response = await fixture.SendAsync(
            owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", CatalogApi.ProductBody("first-product"), CatalogApi.Key());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var product = await CatalogApi.ReadAsync(response);
        Assert.Equal(tenantId, product.GetProperty("tenantId").GetGuid());
        Assert.Equal("Draft", product.GetProperty("status").GetString());
        Assert.Equal(1, product.GetProperty("version").GetInt64());
        Assert.Equal("\"1\"", response.Headers.ETag?.ToString());
        Assert.Equal($"/api/v1/catalog/tenants/{tenantId}/products/{product.GetProperty("id").GetGuid()}", response.Headers.Location?.OriginalString);
        Assert.Equal(19.99m, product.GetProperty("variants")[0].GetProperty("price").GetProperty("amount").GetDecimal());
        Assert.False(product.TryGetProperty("creation", out _));
    }

    [Theory]
    [InlineData("tenantId")]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("stock")]
    public async Task UnknownMembers_AreRefused_SoNothingCanBeOverPosted(string member)
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var body = JsonSerializer.SerializeToNode(CatalogApi.ProductBody($"over-{member.ToLowerInvariant()}"), CatalogApi.Json)!.AsObject();
        body[member] = member == "tenantId" ? Guid.CreateVersion7().ToString() : "x";

        using var response = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", body, CatalogApi.Key());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownNestedMembers_AreRefused()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var body = new
        {
            slug = "nested-over",
            name = "Nested",
            variants = new[] { new { sku = "N-1", price = new { amount = 1m, currency = "EUR" }, stockQuantity = 5 } },
        };

        using var response = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", body, CatalogApi.Key());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Creation_RequiresAnIdempotencyKey_AndReplaysOrRefusesItsReuse()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var path = $"/api/v1/catalog/tenants/{tenantId}/products";

        using var missing = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("keyed"));
        var problem = await CatalogApi.AssertProblemAsync(missing, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));

        var key = CatalogApi.Key();
        using var first = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("keyed"), key);
        using var replay = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("keyed"), key);
        using var reuse = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("keyed-other"), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal((await CatalogApi.ReadAsync(first)).GetProperty("id").GetGuid(), (await CatalogApi.ReadAsync(replay)).GetProperty("id").GetGuid());
        await CatalogApi.AssertProblemAsync(reuse, HttpStatusCode.UnprocessableEntity, "idempotency-key-reused");
    }

    [Fact]
    public async Task ConcurrentRetries_OfOneCreation_CreateOneProduct()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var key = CatalogApi.Key();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", CatalogApi.ProductBody("concurrent"), key)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
            var ids = await Task.WhenAll(responses.Select(async response => (await CatalogApi.ReadAsync(response)).GetProperty("id").GetGuid()));
            Assert.Single(ids.Distinct());
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task InvalidContent_ReportsEveryFieldError()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var body = new
        {
            slug = "Not A Slug",
            name = "",
            variants = new[] { new { sku = "has space", price = new { amount = -1m, currency = "XYZ" } } },
        };

        using var response = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", body, CatalogApi.Key());

        var errors = (await CatalogApi.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed")).GetProperty("errors");
        foreach (var field in new[] { "slug", "name", "variants[0].sku", "variants[0].amount", "variants[0].currency" })
        {
            Assert.True(errors.TryGetProperty(field, out _), field);
        }
    }

    [Fact]
    public async Task SlugsAndSkus_AreUniquePerTenant()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var otherTenantId = fixture.NewTenant(out var otherOwner, out _);
        var path = $"/api/v1/catalog/tenants/{tenantId}/products";
        await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("unique-slug", sku: "UNIQUE-1"));

        using var slug = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("unique-slug", sku: "UNIQUE-2"), CatalogApi.Key());
        using var sku = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("other-slug", sku: "unique-1"), CatalogApi.Key());
        await CatalogApi.AssertProblemAsync(slug, HttpStatusCode.Conflict, "slug-taken");
        await CatalogApi.AssertProblemAsync(sku, HttpStatusCode.Conflict, "sku-taken");

        await fixture.CreateProductAsync(otherTenantId, otherOwner, CatalogApi.ProductBody("unique-slug", sku: "UNIQUE-1"));
    }

    [Fact]
    public async Task References_MustExist_BeActive_AndBelongToTheTenant()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var otherTenantId = fixture.NewTenant(out var otherOwner, out _);
        var retired = await fixture.SeedCategoryAsync(retired: true);
        var foreignBrand = await CreateBrandAsync(otherTenantId, otherOwner, "foreign-brand");
        var path = $"/api/v1/catalog/tenants/{tenantId}/products";

        using var unknownCategory = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("ref-1", [Guid.CreateVersion7()]), CatalogApi.Key());
        using var retiredCategory = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("ref-2", [retired.Id]), CatalogApi.Key());
        using var otherBrand = await fixture.SendAsync(owner, HttpMethod.Post, path, CatalogApi.ProductBody("ref-3", brandId: foreignBrand), CatalogApi.Key());

        Assert.True((await CatalogApi.AssertProblemAsync(unknownCategory, HttpStatusCode.BadRequest, "validation-failed")).GetProperty("errors").TryGetProperty("categoryIds", out _));
        Assert.True((await CatalogApi.AssertProblemAsync(retiredCategory, HttpStatusCode.BadRequest, "validation-failed")).GetProperty("errors").TryGetProperty("categoryIds", out _));
        Assert.True((await CatalogApi.AssertProblemAsync(otherBrand, HttpStatusCode.BadRequest, "validation-failed")).GetProperty("errors").TryGetProperty("brandId", out _));
    }

    [Fact]
    public async Task Updates_RequireTheCurrentEntityTag()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out var staff);
        var product = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("tagged"));
        var path = $"/api/v1/catalog/tenants/{tenantId}/products/{product.GetProperty("id").GetGuid()}";
        var changed = CatalogApi.ProductBody("tagged", name: "Renamed product");

        using var missing = await fixture.SendAsync(staff, HttpMethod.Put, path, changed);
        using var malformed = await fixture.SendAsync(staff, HttpMethod.Put, path, changed, new Dictionary<string, string> { ["If-Match"] = "*" });
        using var stale = await fixture.SendAsync(staff, HttpMethod.Put, path, changed, CatalogApi.IfMatch(7));
        using var current = await fixture.SendAsync(staff, HttpMethod.Put, path, changed, CatalogApi.IfMatch(1));

        await CatalogApi.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "precondition-required");
        await CatalogApi.AssertProblemAsync(malformed, HttpStatusCode.PreconditionFailed, "precondition-failed");
        await CatalogApi.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "precondition-failed");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        var updated = await CatalogApi.ReadAsync(current);
        Assert.Equal(("Renamed product", 2L), (updated.GetProperty("name").GetString(), updated.GetProperty("version").GetInt64()));
        Assert.Equal("\"2\"", current.Headers.ETag?.ToString());
        Assert.Equal(tenantId, updated.GetProperty("tenantId").GetGuid());
    }

    [Fact]
    public async Task AVariantId_IsKeptAcrossUpdates()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var product = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("variant-ids"));
        var variantId = product.GetProperty("variants")[0].GetProperty("id").GetGuid();
        var body = new
        {
            slug = "variant-ids",
            name = "Variant IDs",
            variants = new object[]
            {
                new { id = variantId, sku = "VID-M", options = new[] { new { key = "size", value = "M" } }, price = new { amount = 25m, currency = "EUR" } },
                new { sku = "VID-L", options = new[] { new { key = "size", value = "L" } }, price = new { amount = 27m, currency = "EUR" } },
            },
        };

        using var response = await fixture.SendAsync(
            owner, HttpMethod.Put, $"/api/v1/catalog/tenants/{tenantId}/products/{product.GetProperty("id").GetGuid()}", body, CatalogApi.IfMatch(1));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var variants = (await CatalogApi.ReadAsync(response)).GetProperty("variants");
        Assert.Equal(variantId, variants[0].GetProperty("id").GetGuid());
        Assert.NotEqual(variantId, variants[1].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task TheLifecycle_RunsThroughItsCommands_WithPublicationRequirements()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var category = await fixture.SeedCategoryAsync();
        var incomplete = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("no-category"));
        var product = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("complete", [category.Id]));
        var path = $"/api/v1/catalog/tenants/{tenantId}/products/{product.GetProperty("id").GetGuid()}";

        using (var refused = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products/{incomplete.GetProperty("id").GetGuid()}/publish"))
        {
            var problem = await CatalogApi.AssertProblemAsync(refused, HttpStatusCode.Conflict, "publication-requirements-not-met");
            Assert.Equal("category-required", problem.GetProperty("requirements")[0].GetString());
        }

        Assert.Equal("Published", await CommandAsync(owner, path, "publish", HttpStatusCode.OK));
        Assert.Equal("Published", await CommandAsync(owner, path, "publish", HttpStatusCode.OK));
        using (var removeCategory = await fixture.SendAsync(owner, HttpMethod.Put, path, CatalogApi.ProductBody("complete"), CatalogApi.IfMatch(2)))
        {
            await CatalogApi.AssertProblemAsync(removeCategory, HttpStatusCode.Conflict, "publication-requirements-not-met");
        }

        Assert.Equal("Draft", await CommandAsync(owner, path, "unpublish", HttpStatusCode.OK));
        Assert.Equal("Archived", await CommandAsync(owner, path, "archive", HttpStatusCode.OK));
        using (var archivedChange = await fixture.SendAsync(owner, HttpMethod.Put, path, CatalogApi.ProductBody("complete", [category.Id]), CatalogApi.IfMatch(4)))
        {
            await CatalogApi.AssertProblemAsync(archivedChange, HttpStatusCode.Conflict, "product-archived");
        }

        using (var archivedPublish = await fixture.SendAsync(owner, HttpMethod.Post, $"{path}/publish"))
        {
            await CatalogApi.AssertProblemAsync(archivedPublish, HttpStatusCode.Conflict, "invalid-transition");
        }

        Assert.Equal("Draft", await CommandAsync(owner, path, "restore", HttpStatusCode.OK));
    }

    [Fact]
    public async Task ARetiredBrand_BlocksPublication_ButStaysReferenced()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var category = await fixture.SeedCategoryAsync();
        var brandId = await CreateBrandAsync(tenantId, owner, "retiring-brand");
        var product = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("branded", [category.Id], brandId: brandId));
        using (var retire = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/brands/{brandId}/retire"))
        {
            Assert.Equal(HttpStatusCode.OK, retire.StatusCode);
        }

        using var publish = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products/{product.GetProperty("id").GetGuid()}/publish");

        var problem = await CatalogApi.AssertProblemAsync(publish, HttpStatusCode.Conflict, "publication-requirements-not-met");
        Assert.Equal("brand-inactive", problem.GetProperty("requirements")[0].GetString());
    }

    [Fact]
    public async Task Media_IsReplacedAsAnOrderedList_OfSafeReferences()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out var staff);
        var product = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("with-media"));
        var path = $"/api/v1/catalog/tenants/{tenantId}/products/{product.GetProperty("id").GetGuid()}/media";

        using var unsafeUrl = await fixture.SendAsync(staff, HttpMethod.Put, path, new { media = new[] { new { url = "https://169.254.169.254/latest", contentType = "image/png" } } }, CatalogApi.IfMatch(1));
        using var valid = await fixture.SendAsync(
            staff,
            HttpMethod.Put,
            path,
            new { media = new[] { new { url = "https://cdn.example.com/a.jpg", altText = "Front", contentType = "image/jpeg" }, new { url = "https://cdn.example.com/b.webp", altText = "Back", contentType = "image/webp" } } },
            CatalogApi.IfMatch(1));

        Assert.True((await CatalogApi.AssertProblemAsync(unsafeUrl, HttpStatusCode.BadRequest, "validation-failed")).GetProperty("errors").TryGetProperty("media[0].url", out _));
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var media = (await CatalogApi.ReadAsync(valid)).GetProperty("media");
        Assert.Equal(["Front", "Back"], media.EnumerateArray().Select(item => item.GetProperty("altText").GetString()));
    }

    [Fact]
    public async Task TheMerchantListing_FiltersSortsAndPages()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out var staff);
        var category = await fixture.SeedCategoryAsync();
        for (var i = 0; i < 5; i++)
        {
            var created = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody($"listed-{i}", [category.Id], sku: $"LIST-{i}", name: i == 3 ? "Unusual teapot" : $"Plain item {i}"));
            await CatalogApi.PassAsync(created.GetProperty("updatedAt").GetDateTimeOffset());
        }

        var published = await fixture.CreateProductAsync(tenantId, owner, CatalogApi.ProductBody("listed-published", [category.Id], sku: "LIST-P"));
        await CatalogApi.PassAsync(published.GetProperty("updatedAt").GetDateTimeOffset());
        await CommandAsync(owner, $"/api/v1/catalog/tenants/{tenantId}/products/{published.GetProperty("id").GetGuid()}", "publish", HttpStatusCode.OK);
        var basePath = $"/api/v1/catalog/tenants/{tenantId}/products";

        var page = await GetAsync(staff, $"{basePath}?pageSize=2&page=2&sort=oldest");
        Assert.Equal((2, 2, 6L), (page.GetProperty("page").GetInt32(), page.GetProperty("pageSize").GetInt32(), page.GetProperty("totalCount").GetInt64()));
        Assert.Equal(["listed-2", "listed-3"], page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("slug").GetString()));

        Assert.Equal(["listed-published"], Slugs(await GetAsync(staff, $"{basePath}?status=Published")));
        Assert.Equal(5, (await GetAsync(staff, $"{basePath}?status=Draft")).GetProperty("totalCount").GetInt64());
        Assert.Equal(["listed-4"], Slugs(await GetAsync(staff, $"{basePath}?sku=list-4")));
        Assert.Equal(["listed-3"], Slugs(await GetAsync(staff, $"{basePath}?q=teapot")));
        Assert.Equal("listed-published", Slugs(await GetAsync(staff, basePath))[0]);

        foreach (var invalid in new[] { "sort=name", "status=draft", "pageSize=101", "page=0", "q=a", "sku=a%20b" })
        {
            using var response = await fixture.SendAsync(staff, HttpMethod.Get, $"{basePath}?{invalid}");
            await CatalogApi.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        }
    }

    [Fact]
    public async Task StaffEdit_ButOnlyOwnersAndAdminsPublish()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out var staff);
        var category = await fixture.SeedCategoryAsync();
        var product = await fixture.CreateProductAsync(tenantId, staff, CatalogApi.ProductBody("staff-made", [category.Id]));
        var path = $"/api/v1/catalog/tenants/{tenantId}/products/{product.GetProperty("id").GetGuid()}";

        using (var publish = await fixture.SendAsync(staff, HttpMethod.Post, $"{path}/publish"))
        {
            await CatalogApi.AssertProblemAsync(publish, HttpStatusCode.Forbidden, "permission-denied");
        }

        Assert.Equal("Published", await CommandAsync($"admin-{tenantId:N}", path, "publish", HttpStatusCode.OK));
    }

    private static string?[] Slugs(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("slug").GetString())];

    private async Task<JsonElement> GetAsync(string subject, string path)
    {
        using var response = await fixture.SendAsync(subject, HttpMethod.Get, path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return await CatalogApi.ReadAsync(response);
    }

    private async Task<string?> CommandAsync(string subject, string productPath, string command, HttpStatusCode expected)
    {
        using var response = await fixture.SendAsync(subject, HttpMethod.Post, $"{productPath}/{command}");
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync(Token));
        return (await CatalogApi.ReadAsync(response)).GetProperty("status").GetString();
    }

    private async Task<Guid> CreateBrandAsync(Guid tenantId, string owner, string slug)
    {
        using var response = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/brands", new { slug, name = slug });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await CatalogApi.ReadAsync(response)).GetProperty("id").GetGuid();
    }
}
