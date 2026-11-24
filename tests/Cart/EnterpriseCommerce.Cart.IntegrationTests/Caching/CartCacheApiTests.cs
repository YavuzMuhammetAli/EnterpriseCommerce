using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Cart.Infrastructure.Caching;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace EnterpriseCommerce.Cart.IntegrationTests.Caching;

/// <summary>The host with the cache on: the real Redis, its own key prefix.</summary>
public sealed class CartCacheApiFixture : CartApiFixture
{
    public RedisTestServer Redis { get; } = new();

    public override void RequireAvailable()
    {
        base.RequireAvailable();
        RedisTestServer.RequireAvailable();
    }

    /// <summary>The host is started and its cache connected before the tests, which assert what Redis holds (finding F2).</summary>
    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (Database.IsConfigured && RedisTestServer.IsConfigured)
        {
            using var client = CreateClient();
            await Eventually.TrueAsync(
                async () =>
                {
                    using var health = await client.GetAsync("/health/cache", TestContext.Current.CancellationToken);
                    return await health.Content.ReadAsStringAsync(TestContext.Current.CancellationToken) == "Healthy";
                },
                TimeSpan.FromSeconds(30),
                "the host's cache Redis is connected");
        }
    }

    protected override void ConfigureCache(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("Cart:Cache:Enabled", "true");
        builder.UseSetting("ConnectionStrings:CartCache", RedisTestServer.IsConfigured ? RedisTestServer.ConnectionString() : $"127.0.0.1:{RedisTestServer.ClosedPort()}");
        builder.UseSetting("Cart:Cache:KeyPrefix", Redis.KeyPrefix);
        builder.UseSetting("Cart:Cache:CircuitBreakDuration", "00:00:01");
        builder.UseSetting("Cart:Cache:RepairInterval", "00:00:00.200");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await Redis.DisposeAsync();
    }
}

/// <summary>
/// The cache in the host (ADR-030): reads hit it, mutations write through it, and SQL Server stays authoritative when
/// Redis is flushed or down.
/// </summary>
public sealed class CartCacheApiTests(CartCacheApiFixture fixture) : IClassFixture<CartCacheApiFixture>
{
    [Fact]
    public async Task AReadAfterAWrite_IsServedFromTheCache()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var added = await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id);
        Assert.True(await fixture.Redis.Database.KeyExistsAsync(RedisCartCache.KeyFor(fixture.Redis.KeyPrefix, customer)), "The add wrote through.");

        // Prove the hit: the row is gone from SQL Server, and the read still answers the cached cart.
        await fixture.Database.ExecuteAsync($"DELETE FROM Carts WHERE CustomerSubject = N'{customer}'");
        var cached = await fixture.GetCartAsync(customer);

        Assert.Equal(added.GetRawText(), cached.GetRawText());
    }

    [Fact]
    public async Task AMissIsReadFromSqlServer_AndRepopulatesTheCache()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var added = await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id);
        var key = RedisCartCache.KeyFor(fixture.Redis.KeyPrefix, customer);
        await fixture.Redis.Database.KeyDeleteAsync(key);

        var read = await fixture.GetCartAsync(customer);

        Assert.Equal(added.GetProperty("version").GetInt32(), read.GetProperty("version").GetInt32());
        Assert.True(await fixture.Redis.Database.KeyExistsAsync(key));
    }

    [Fact]
    public async Task EveryMutation_IsVisibleToTheNextRead()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var merchant = Guid.CreateVersion7();
        var first = StubVariant.New();
        var second = StubVariant.New();
        var product = fixture.Catalog.Publish(merchant, first, second);
        var cart = await fixture.AddAsync(customer, product, first.Id);
        var line = cart.GetProperty("items")[0].GetProperty("id").GetGuid();
        await fixture.GetCartAsync(customer);

        await fixture.AddAsync(customer, product, second.Id);
        Assert.Equal(2, (await fixture.GetCartAsync(customer)).GetProperty("lineCount").GetInt32());

        using (var set = await fixture.SendAsync(customer, HttpMethod.Put, $"/api/v1/cart/items/{line}", new { quantity = 4 }))
        {
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        }

        Assert.Equal(4, (await fixture.GetCartAsync(customer)).GetProperty("items")[0].GetProperty("quantity").GetInt32());

        using (var clear = await fixture.SendAsync(customer, HttpMethod.Delete, "/api/v1/cart/items"))
        {
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        }

        Assert.Equal(0, (await fixture.GetCartAsync(customer)).GetProperty("lineCount").GetInt32());
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_EndWithTheCommittedCartInTheCache()
    {
        fixture.RequireAvailable();
        for (var round = 0; round < 5; round++)
        {
            var customer = CartApiFixture.NewCustomer();
            var variant = StubVariant.New();
            var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
            await fixture.AddAsync(customer, product, variant.Id);
            await fixture.Redis.Database.KeyDeleteAsync(RedisCartCache.KeyFor(fixture.Redis.KeyPrefix, customer));
            using var start = new Barrier(16);

            await Task.WhenAll(Enumerable.Range(0, 16).Select(caller => Task.Run(async () =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(30));
                using var response = caller % 2 == 0
                    ? await fixture.SendAsync(customer, HttpMethod.Get, "/api/v1/cart")
                    : await fixture.SendAsync(customer, HttpMethod.Post, "/api/v1/cart/items", new { productId = product, variantId = variant.Id, quantity = 1 });
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, response.StatusCode.ToString());
            })));

            var stored = await fixture.Database.ScalarAsync<int>($"SELECT Version FROM Carts WHERE CustomerSubject = N'{customer}'");
            Assert.Equal(stored, (await fixture.GetCartAsync(customer)).GetProperty("version").GetInt32());
        }
    }

    [Fact]
    public async Task AFlushedCache_ChangesNothingButLatency()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var added = await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id, 3);

        await foreach (var key in ScanAsync())
        {
            await fixture.Redis.Database.KeyDeleteAsync(key);
        }

        Assert.Equal(added.GetRawText(), (await fixture.GetCartAsync(customer)).GetRawText());
    }

    [Fact]
    public async Task AnExpiredCachedCart_ReadsAsEmpty()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id);
        await fixture.Database.ExecuteAsync($"DELETE FROM Carts WHERE CustomerSubject = N'{customer}'");

        // The cleanup deleted the expired row; only the cache still holds it, until its TTL.
        fixture.Time.Advance(TimeSpan.FromDays(31));
        try
        {
            Assert.Equal(JsonValueKind.Null, (await fixture.GetCartAsync(customer)).GetProperty("id").ValueKind);
        }
        finally
        {
            fixture.Time.Advance(TimeSpan.FromDays(-31));
        }
    }

    [Fact]
    public async Task WithRedisUnreachable_EveryOperationWorks_FromSqlServer()
    {
        fixture.RequireAvailable();
        await using var factory = fixture.CreateFactory(builder => builder.UseSetting("ConnectionStrings:CartCache", RedisTestServer.ConnectionString(RedisTestServer.ClosedPort())));
        var customer = CartApiFixture.NewCustomer();
        using var client = CartApiFixture.CreateClient(factory, fixture.CustomerToken(customer));
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
        var watch = Stopwatch.StartNew();

        using var add = await client.PostAsync("/api/v1/cart/items", JsonContent(new { productId = product, variantId = variant.Id, quantity = 2 }), TestContext.Current.CancellationToken);
        using var read = await client.GetAsync("/api/v1/cart", TestContext.Current.CancellationToken);
        var line = (await CartApiFixture.ReadAsync(read)).GetProperty("items")[0].GetProperty("id").GetGuid();
        using var set = await client.PutAsync($"/api/v1/cart/items/{line}", JsonContent(new { quantity = 5 }), TestContext.Current.CancellationToken);
        using var reread = await client.GetAsync("/api/v1/cart", TestContext.Current.CancellationToken);
        using var ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        using var cache = await client.GetAsync("/health/cache", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal(5, (await CartApiFixture.ReadAsync(reread)).GetProperty("items")[0].GetProperty("quantity").GetInt32());
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Degraded", await cache.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"An unreachable cache must not hold requests: {watch.Elapsed}.");
    }

    [Fact]
    public async Task TheCacheHealth_IsHealthy_WhenRedisAnswers()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient();

        using var cache = await client.GetAsync("/health/cache", TestContext.Current.CancellationToken);

        Assert.Equal("Healthy", await cache.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");

    private async IAsyncEnumerable<StackExchange.Redis.RedisKey> ScanAsync()
    {
        var multiplexer = fixture.Redis.Database.Multiplexer;
        foreach (var endpoint in multiplexer.GetEndPoints())
        {
            await foreach (var key in multiplexer.GetServer(endpoint).KeysAsync(pattern: fixture.Redis.KeyPrefix + "*"))
            {
                yield return key;
            }
        }
    }
}
