using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Inventory.UnitTests.Infrastructure;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Api;

/// <summary>
/// The merchant stock management API on the real host and SQL Server (INVENTORY_API_CONTRACT.md sections 3 to 6, ADR-026
/// sections 2, 4, 5, 8 and 11): item creation with Catalog's verdict, adjustments with reasons, idempotency, the ledger,
/// listing and paging.
/// </summary>
public sealed class MerchantInventoryApiTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatingAnItem_RecordsTheInitialStock_TheSkuSnapshot_AndItsEvent()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var product = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        fixture.Dependencies.SetVariant(tenantId, product, variant, status: "Draft", sku: "TSHIRT-BLUE-M", active: false);

        using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 25 }, InventoryApiFixture.Key());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var item = await InventoryApiFixture.ReadAsync(response);
        Assert.Equal((25, 0, 25, 1L, "TSHIRT-BLUE-M"), (item.GetProperty("onHand").GetInt32(), item.GetProperty("reserved").GetInt32(), item.GetProperty("available").GetInt32(), item.GetProperty("version").GetInt64(), item.GetProperty("sku").GetString()));
        Assert.Equal($"/api/v1/inventory/tenants/{tenantId}/items/{item.GetProperty("id").GetGuid()}", response.Headers.Location?.OriginalString);

        var movements = await ListAsync(owner, $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}/movements");
        var initial = Assert.Single(movements.GetProperty("items").EnumerateArray());
        Assert.Equal(("InitialStock", 25, "User", owner), (initial.GetProperty("type").GetString(), initial.GetProperty("onHandAfter").GetInt32(), initial.GetProperty("actorKind").GetString(), initial.GetProperty("actorId").GetString()));
        EventSchemas.AssertValid(await fixture.Database.ScalarAsync<string>($"SELECT Envelope FROM OutboxMessages WHERE MessageKey = '{tenantId}'"));
    }

    [Fact]
    public async Task ACreation_IsIdempotent_PerKey_AndAReusedKeyWithAnotherBodyIsRefused()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var product = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        fixture.Dependencies.SetVariant(tenantId, product, variant);
        var key = InventoryApiFixture.Key();

        using var first = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 5 }, key);
        using var replay = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 5 }, key);
        using var reused = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 6 }, key);
        using var second = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 5 }, InventoryApiFixture.Key());

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, replay.StatusCode));
        Assert.Equal((await InventoryApiFixture.ReadAsync(first)).GetProperty("id").GetGuid(), (await InventoryApiFixture.ReadAsync(replay)).GetProperty("id").GetGuid());
        await InventoryApiFixture.AssertProblemAsync(reused, HttpStatusCode.UnprocessableEntity, "idempotency-key-reused");
        await InventoryApiFixture.AssertProblemAsync(second, HttpStatusCode.Conflict, "inventory-item-exists");
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM InventoryItems WHERE TenantId = '{tenantId}'"));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE MessageKey = '{tenantId}'"));
    }

    [Fact]
    public async Task OnlyAVariantCatalogConfirms_InANonArchivedProductOfTheTenant_GetsAnItem()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var product = Guid.CreateVersion7();
        var archived = Guid.CreateVersion7();
        var unknownStatus = Guid.CreateVersion7();
        fixture.Dependencies.SetVariant(tenantId, product, archived, status: "Archived");
        fixture.Dependencies.SetVariant(tenantId, product, unknownStatus, status: "Quarantined");
        var foreignTenant = Guid.CreateVersion7();
        var foreignVariant = Guid.CreateVersion7();
        fixture.Dependencies.SetVariant(foreignTenant, product, foreignVariant);

        using var unknown = await CreateAsync(tenantId, owner, product, Guid.CreateVersion7());
        using var ofAnotherTenant = await CreateAsync(tenantId, owner, product, foreignVariant);
        using var archivedProduct = await CreateAsync(tenantId, owner, product, archived);
        using var unknownProductStatus = await CreateAsync(tenantId, owner, product, unknownStatus);

        await InventoryApiFixture.AssertProblemAsync(unknown, HttpStatusCode.UnprocessableEntity, "variant-not-found");
        await InventoryApiFixture.AssertProblemAsync(ofAnotherTenant, HttpStatusCode.UnprocessableEntity, "variant-not-found");
        await InventoryApiFixture.AssertProblemAsync(archivedProduct, HttpStatusCode.Conflict, "variant-not-eligible");
        await InventoryApiFixture.AssertProblemAsync(unknownProductStatus, HttpStatusCode.Conflict, "variant-not-eligible");
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM InventoryItems WHERE TenantId = '{tenantId}'"));
    }

    [Fact]
    public async Task Adjustments_ChangeOnHandStock_WithAReason_AndAreRecordedInTheLedger()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out var admin, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var path = $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}";

        var received = await AdjustAsync(admin, path, 15, "Receipt", HttpStatusCode.Created);
        var damaged = await AdjustAsync(owner, path, -3, "Damage", HttpStatusCode.Created);
        var counted = await AdjustAsync(owner, path, -2, "CycleCount", HttpStatusCode.Created);

        Assert.Equal((25, 2L), (received.GetProperty("item").GetProperty("onHand").GetInt32(), received.GetProperty("item").GetProperty("version").GetInt64()));
        Assert.Equal((22, "Damage", 25, 22), (damaged.GetProperty("item").GetProperty("onHand").GetInt32(), damaged.GetProperty("movement").GetProperty("reason").GetString(), damaged.GetProperty("movement").GetProperty("onHandBefore").GetInt32(), damaged.GetProperty("movement").GetProperty("onHandAfter").GetInt32()));
        Assert.Equal((20, 4L), (counted.GetProperty("item").GetProperty("onHand").GetInt32(), counted.GetProperty("item").GetProperty("version").GetInt64()));

        var movements = await ListAsync(owner, $"{path}/movements");
        Assert.Equal([4L, 3L, 2L, 1L], movements.GetProperty("items").EnumerateArray().Select(movement => movement.GetProperty("itemVersion").GetInt64()));
        Assert.Equal(4, movements.GetProperty("totalCount").GetInt64());
        Assert.Equal(4, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE MessageKey = '{tenantId}' AND EventType = 'StockLevelChanged'"));
    }

    [Theory]
    [InlineData(0, "Correction", "quantityChange")]
    [InlineData(1000000001, "Receipt", "quantityChange")]
    [InlineData(-1, "Receipt", "quantityChange")]
    [InlineData(1, "Damage", "quantityChange")]
    [InlineData(1, "receipt", "reason")]
    [InlineData(1, "Gift", "reason")]
    [InlineData(1, null, "reason")]
    public async Task AnInvalidAdjustment_IsRefused_AndChangesNothing(int change, string? reason, string field)
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var path = $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}";

        using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, $"{path}/adjustments", new { quantityChange = change, reason }, InventoryApiFixture.Key());

        var problem = await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
        Assert.Equal(1, (await GetAsync(owner, path)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task AnAdjustment_NeverTakesStockBelowZeroOrAboveTheMaximum()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 3);
        var full = await fixture.CreateItemAsync(tenantId, owner, onHand: 999_999_999);

        using var below = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}/adjustments", new { quantityChange = -4, reason = "Loss" }, InventoryApiFixture.Key());
        using var above = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, $"{Items(tenantId)}/{full.GetProperty("id").GetGuid()}/adjustments", new { quantityChange = 2, reason = "Receipt" }, InventoryApiFixture.Key());

        await InventoryApiFixture.AssertProblemAsync(below, HttpStatusCode.Conflict, "insufficient-unreserved-stock");
        await InventoryApiFixture.AssertProblemAsync(above, HttpStatusCode.Conflict, "stock-limit-exceeded");
    }

    [Fact]
    public async Task AnAdjustment_IsIdempotentPerKey_AndAReusedKeyIsRefusedForAnotherChangeOrItem()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var other = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var path = $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}/adjustments";
        var key = InventoryApiFixture.Key();

        using var first = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, path, new { quantityChange = 5, reason = "Receipt" }, key);
        using var replay = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, path, new { quantityChange = 5, reason = "Receipt" }, key);
        using var otherChange = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, path, new { quantityChange = 6, reason = "Receipt" }, key);
        using var otherItem = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, $"{Items(tenantId)}/{other.GetProperty("id").GetGuid()}/adjustments", new { quantityChange = 5, reason = "Receipt" }, key);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, replay.StatusCode));
        Assert.Equal((await InventoryApiFixture.ReadAsync(first)).GetProperty("movement").GetProperty("id").GetGuid(), (await InventoryApiFixture.ReadAsync(replay)).GetProperty("movement").GetProperty("id").GetGuid());
        await InventoryApiFixture.AssertProblemAsync(otherChange, HttpStatusCode.UnprocessableEntity, "idempotency-key-reused");
        await InventoryApiFixture.AssertProblemAsync(otherItem, HttpStatusCode.UnprocessableEntity, "idempotency-key-reused");
        Assert.Equal(15, (await GetAsync(owner, $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}")).GetProperty("onHand").GetInt32());
        Assert.Equal(10, (await GetAsync(owner, $"{Items(tenantId)}/{other.GetProperty("id").GetGuid()}")).GetProperty("onHand").GetInt32());
    }

    [Fact]
    public async Task ConcurrentRetriesOfOneAdjustment_ApplyItOnce()
    {
        fixture.RequireAvailable();
        const int Retries = 8;
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var path = $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}";
        var key = InventoryApiFixture.Key();
        using var start = new Barrier(Retries);

        var statuses = await Task.WhenAll(Enumerable.Range(0, Retries).Select(_ => Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(30));
            using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, $"{path}/adjustments", new { quantityChange = 7, reason = "Receipt" }, key);
            return response.StatusCode;
        })));

        Assert.Single(statuses, status => status == HttpStatusCode.Created);
        Assert.All(statuses, status => Assert.Contains(status, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Equal((17, 2L), ((await GetAsync(owner, path)).GetProperty("onHand").GetInt32(), (await GetAsync(owner, path)).GetProperty("version").GetInt64()));
    }

    [Fact]
    public async Task TheListing_IsTheTenantsOwn_FilteredAndPaged()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out var staff);
        var other = fixture.NewTenant(out var otherOwner, out _, out _);
        var product = Guid.CreateVersion7();
        var first = await fixture.CreateItemAsync(tenantId, owner, 1, productId: product, sku: "LIST-A");
        var second = await fixture.CreateItemAsync(tenantId, owner, 2, productId: product, sku: "LIST-B");
        await fixture.CreateItemAsync(tenantId, owner, 3);
        await fixture.CreateItemAsync(other, otherOwner, 4, productId: product);

        var all = await ListAsync(staff, Items(tenantId));
        var ofProduct = await ListAsync(owner, $"{Items(tenantId)}?productId={product}");
        var bySku = await ListAsync(owner, $"{Items(tenantId)}?sku=LIST-B");
        var byVariant = await ListAsync(owner, $"{Items(tenantId)}?variantId={first.GetProperty("variantId").GetGuid()}");
        var paged = await ListAsync(owner, $"{Items(tenantId)}?page=2&pageSize=2");

        Assert.Equal(3, all.GetProperty("totalCount").GetInt64());
        Assert.Equal([first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid()], ofProduct.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(second.GetProperty("id").GetGuid(), Assert.Single(bySku.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("id").GetGuid(), Assert.Single(byVariant.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal((2, 2, 3L, 1), (paged.GetProperty("page").GetInt32(), paged.GetProperty("pageSize").GetInt32(), paged.GetProperty("totalCount").GetInt64(), paged.GetProperty("items").GetArrayLength()));
    }

    [Theory]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=0")]
    [InlineData("?sku=has%20space")]
    public async Task AnInvalidListingQuery_IsRefused(string query)
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);

        using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Get, Items(tenantId) + query);

        await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Theory]
    [InlineData("""{"productId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","variantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","initialOnHand":1,"tenantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d"}""")]
    [InlineData("""{"productId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","variantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","initialOnHand":1.5}""")]
    [InlineData("""{"productId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","variantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","initialOnHand":1,"onHand":5}""")]
    [InlineData("not json")]
    public async Task AnUnknownMember_AFractionalQuantity_OrMalformedJson_IsRefused(string body)
    {
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), content, InventoryApiFixture.Key());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    public async Task AWriteWithoutAWellFormedIdempotencyKey_IsRefused(string? key)
    {
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var headers = key is null ? null : new Dictionary<string, string> { ["Idempotency-Key"] = key };

        using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = Guid.CreateVersion7(), variantId = Guid.CreateVersion7(), initialOnHand = 1 }, headers);

        var problem = await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task AnUnknownItem_IsNotFound()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);

        using var item = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Get, $"{Items(tenantId)}/{Guid.CreateVersion7()}");
        using var adjustment = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, $"{Items(tenantId)}/{Guid.CreateVersion7()}/adjustments", new { quantityChange = 1, reason = "Receipt" }, InventoryApiFixture.Key());
        using var movements = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Get, $"{Items(tenantId)}/{Guid.CreateVersion7()}/movements");

        await InventoryApiFixture.AssertProblemAsync(item, HttpStatusCode.NotFound, "inventory-item-not-found");
        await InventoryApiFixture.AssertProblemAsync(adjustment, HttpStatusCode.NotFound, "inventory-item-not-found");
        await InventoryApiFixture.AssertProblemAsync(movements, HttpStatusCode.NotFound, "inventory-item-not-found");
    }

    private Task<HttpResponseMessage> CreateAsync(Guid tenantId, string owner, Guid product, Guid variant) =>
        fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 1 }, InventoryApiFixture.Key());

    private async Task<JsonElement> AdjustAsync(string subject, string itemPath, int change, string reason, HttpStatusCode expected)
    {
        using var response = await fixture.SendAsync(fixture.UserToken(subject), HttpMethod.Post, $"{itemPath}/adjustments", new { quantityChange = change, reason }, InventoryApiFixture.Key());
        Assert.True(response.StatusCode == expected, await response.Content.ReadAsStringAsync(Token));
        return await InventoryApiFixture.ReadAsync(response);
    }

    private async Task<JsonElement> GetAsync(string subject, string path)
    {
        using var response = await fixture.SendAsync(fixture.UserToken(subject), HttpMethod.Get, path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return await InventoryApiFixture.ReadAsync(response);
    }

    private Task<JsonElement> ListAsync(string subject, string path) => GetAsync(subject, path);

    private static string Items(Guid tenantId) => $"/api/v1/inventory/tenants/{tenantId}/items";
}
