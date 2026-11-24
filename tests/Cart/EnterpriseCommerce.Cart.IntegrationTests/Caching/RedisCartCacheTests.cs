using System.Diagnostics;
using EnterpriseCommerce.Cart.Application.Carts;
using EnterpriseCommerce.Cart.Domain.Carts;
using EnterpriseCommerce.Cart.Infrastructure.Caching;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Cart.IntegrationTests.Caching;

/// <summary>
/// The cart cache against a real Redis (ADR-030): hits and misses, ordered writes, the TTL, races, outages and
/// recovery. Outages are real: a refused port, or a relay that is cut and restored.
/// </summary>
public sealed class RedisCartCacheTests : IAsyncLifetime
{
    private readonly RedisTestServer _redis = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _redis.DisposeAsync();

    [Fact]
    public async Task AMiss_ThenAStore_ThenAHit_ReturnsTheSameView()
    {
        RedisTestServer.RequireAvailable();
        using var under = await _redis.CreateCache().ConnectedAsync();
        var subject = NewSubject();
        var view = View(stamp: 10, version: 1);

        Assert.Null(await under.Cache.GetAsync(subject, Token));
        await under.Cache.StoreAsync(subject, view, CartCacheWrite.AfterRead, Token);
        var hit = await under.Cache.GetAsync(subject, Token);

        Assert.NotNull(hit);
        Assert.Equal(Json(view), Json(hit));
    }

    [Fact]
    public async Task TheKey_HashesTheSubject_AndEveryWriteSetsTheTtl()
    {
        RedisTestServer.RequireAvailable();
        using var under = await _redis.CreateCache(configure: options => options.TimeToLive = TimeSpan.FromMinutes(15)).ConnectedAsync();
        var subject = NewSubject();

        await under.Cache.StoreAsync(subject, View(stamp: 1), CartCacheWrite.AfterRead, Token);

        var key = RedisCartCache.KeyFor(_redis.KeyPrefix, subject);
        Assert.DoesNotContain(subject, key.ToString(), StringComparison.Ordinal);
        Assert.Matches($"^{_redis.KeyPrefix}cart:v1:[0-9a-f]{{64}}$", key.ToString());
        var ttl = await _redis.Database.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.Equal("0000000000000001", (string?)await _redis.Database.HashGetAsync(key, "rv"));
    }

    [Fact]
    public async Task AnOlderView_NeverReplacesANewerOne()
    {
        RedisTestServer.RequireAvailable();
        using var under = await _redis.CreateCache().ConnectedAsync();
        var subject = NewSubject();

        await under.Cache.StoreAsync(subject, View(stamp: 20, version: 2), CartCacheWrite.AfterCommit, Token);
        await under.Cache.StoreAsync(subject, View(stamp: 10, version: 1), CartCacheWrite.AfterRead, Token);
        await under.Cache.StoreAsync(subject, View(stamp: 20, version: 2, quantity: 9), CartCacheWrite.AfterRead, Token);

        var cached = await under.Cache.GetAsync(subject, Token);
        Assert.Equal(2, cached!.Version);
        Assert.Equal(1, cached.Items[0].Quantity);
    }

    [Fact]
    public async Task AReadThatLoadedTheOldCart_CannotOverwriteTheMutationThatCommittedMeanwhile()
    {
        RedisTestServer.RequireAvailable();
        using var under = await _redis.CreateCache().ConnectedAsync();
        var subject = NewSubject();

        // 1. A read misses and loads version 1 from SQL Server (stamp 100); it is slow to write it back.
        var loadedByTheRead = View(stamp: 100, version: 1);

        // 2. Meanwhile a mutation commits version 2 (stamp 101) and writes it through.
        await under.Cache.StoreAsync(subject, View(stamp: 101, version: 2), CartCacheWrite.AfterCommit, Token);

        // 3. The slow read finally writes what it loaded: the delete-then-repopulate race of ADR-030.
        await under.Cache.StoreAsync(subject, loadedByTheRead, CartCacheWrite.AfterRead, Token);

        Assert.Equal(2, (await under.Cache.GetAsync(subject, Token))!.Version);
    }

    [Fact]
    public async Task ConcurrentWritesInAnyOrder_LeaveTheNewestView()
    {
        RedisTestServer.RequireAvailable();
        using var under = await _redis.CreateCache().ConnectedAsync();
        for (var round = 0; round < 10; round++)
        {
            var subject = NewSubject();
            var stamps = Enumerable.Range(1, 32).OrderBy(_ => Random.Shared.Next()).ToArray();
            using var start = new Barrier(stamps.Length);

            await Task.WhenAll(stamps.Select(stamp => Task.Run(async () =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(30));
                await under.Cache.StoreAsync(subject, View(stamp: stamp, version: stamp), stamp % 2 == 0 ? CartCacheWrite.AfterCommit : CartCacheWrite.AfterRead, Token);
            })));

            Assert.Equal(32, (await under.Cache.GetAsync(subject, Token))!.Version);
        }
    }

    [Fact]
    public async Task AnExpiredEntry_IsAMiss()
    {
        RedisTestServer.RequireAvailable();
        using var under = await _redis.CreateCache().ConnectedAsync();
        var subject = NewSubject();
        await under.Cache.StoreAsync(subject, View(stamp: 5), CartCacheWrite.AfterRead, Token);

        // The minimum TTL is a minute; Redis expires the key exactly the same way when its TTL is shortened.
        await _redis.Database.KeyExpireAsync(RedisCartCache.KeyFor(_redis.KeyPrefix, subject), TimeSpan.FromMilliseconds(50));
        await Task.Delay(200, Token);

        Assert.Null(await under.Cache.GetAsync(subject, Token));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schema\":2,\"cart\":null}")]
    [InlineData("{\"schema\":1}")]
    public async Task AnEntryOfAnotherShape_IsAMiss(string payload)
    {
        RedisTestServer.RequireAvailable();
        using var under = await _redis.CreateCache().ConnectedAsync();
        var subject = NewSubject();
        await _redis.Database.HashSetAsync(RedisCartCache.KeyFor(_redis.KeyPrefix, subject), [new("rv", "0000000000000001"), new("p", payload)]);

        Assert.Null(await under.Cache.GetAsync(subject, Token));
    }

    [Fact]
    public async Task AnEntryWhoseVersionFieldDisagreesWithItsPayload_IsAMiss()
    {
        RedisTestServer.RequireAvailable();
        using var under = await _redis.CreateCache().ConnectedAsync();
        var subject = NewSubject();
        await under.Cache.StoreAsync(subject, View(stamp: 7), CartCacheWrite.AfterRead, Token);
        await _redis.Database.HashSetAsync(RedisCartCache.KeyFor(_redis.KeyPrefix, subject), "rv", "0000000000000008");

        Assert.Null(await under.Cache.GetAsync(subject, Token));
    }

    [Fact]
    public async Task WithRedisDown_NoRequestWaits_ReadsMiss_WritesAreKeptForRepair_AndTheCircuitOpens()
    {
        RedisTestServer.RequireAvailable();
        using var under = _redis.CreateCache(RedisTestServer.ConnectionString(RedisTestServer.ClosedPort()));
        var subject = NewSubject();
        var watch = Stopwatch.StartNew();

        // While the first connection attempt runs, the cache is bypassed: nothing waits for it.
        Assert.Null(await under.Cache.GetAsync(subject, Token));
        await under.Cache.StoreAsync(subject, View(stamp: 3), CartCacheWrite.AfterCommit, Token);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"A request must not wait for the connection: {watch.Elapsed}.");
        Assert.Equal(1, under.Repairs.Count);

        // Once the attempt is over (refused), a command fails fast and opens the circuit.
        await under.Connection.GetDatabaseAsync(Token);
        watch.Restart();
        Assert.Null(await under.Cache.GetAsync(NewSubject(), Token));
        Assert.True(under.Circuit.IsOpen);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"A failed command must cost at most its bound: {watch.Elapsed}.");

        // While the circuit is open Redis is not even tried.
        watch.Restart();
        for (var read = 0; read < 50; read++)
        {
            Assert.Null(await under.Cache.GetAsync(subject, Token));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(500), $"Bypassed reads must not wait: {watch.Elapsed}.");
    }

    [Fact]
    public async Task AfterAnOutage_TheRepairReplaysTheCommittedView_SoTheStaleEntryIsReplaced()
    {
        RedisTestServer.RequireAvailable();
        await using var relay = new TcpRelay(RedisTestServer.Endpoint());
        using var under = await _redis.CreateCache(RedisTestServer.ConnectionString(relay.Port)).ConnectedAsync();
        var subject = NewSubject();
        await under.Cache.StoreAsync(subject, View(stamp: 1, version: 1), CartCacheWrite.AfterCommit, Token);
        Assert.Equal(1, (await under.Cache.GetAsync(subject, Token))!.Version);

        // Redis goes away; a mutation commits version 2 in SQL Server, and its write-through fails.
        relay.Cut();
        await under.Cache.StoreAsync(subject, View(stamp: 2, version: 2), CartCacheWrite.AfterCommit, Token);
        Assert.Equal(1, under.Repairs.Count);

        // While it waits for repair, this instance never serves the stale entry: the read bypasses the cache.
        relay.Restore();
        await Task.Delay(TimeSpan.FromSeconds(1.2), Token);
        Assert.Null(await under.Cache.GetAsync(subject, Token));

        // Redis is back and the circuit closed: the repair replays version 2.
        await Eventually.TrueAsync(async () => await under.Repair.RepairOnceAsync() >= 0 && under.Repairs.Count == 0, TimeSpan.FromSeconds(15), "the repair queue drained");
        Assert.Equal(2, (await under.Cache.GetAsync(subject, Token))!.Version);
        Assert.Equal("0000000000000002", (string?)await _redis.Database.HashGetAsync(RedisCartCache.KeyFor(_redis.KeyPrefix, subject), "rv"));
    }

    [Fact]
    public async Task TheRepairQueue_IsBounded_KeepsTheNewestView_AndDropsWhatTheTtlWouldHaveExpired()
    {
        RedisTestServer.RequireAvailable();
        var time = new MutableTime();
        using var under = _redis.CreateCache(RedisTestServer.ConnectionString(RedisTestServer.ClosedPort()), time, options => options.RepairQueueCapacity = 2);

        await under.Cache.StoreAsync("a", View(stamp: 2), CartCacheWrite.AfterCommit, Token);
        await under.Cache.StoreAsync("a", View(stamp: 1), CartCacheWrite.AfterCommit, Token);
        await under.Cache.StoreAsync("b", View(stamp: 1), CartCacheWrite.AfterCommit, Token);
        await under.Cache.StoreAsync("c", View(stamp: 1), CartCacheWrite.AfterCommit, Token);
        await under.Cache.StoreAsync("d", View(stamp: 1), CartCacheWrite.AfterRead, Token);

        var due = under.Repairs.Due();
        Assert.Equal(2, due.Count);
        Assert.Equal("0000000000000002", due.Single(entry => entry.Key == RedisCartCache.KeyFor(_redis.KeyPrefix, "a").ToString()).Value.View.Stamp);

        time.Advance(TimeSpan.FromMinutes(16));
        Assert.Empty(under.Repairs.Due());
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Json(CartView view) => System.Text.Json.JsonSerializer.Serialize(view, RedisCartCache.PayloadJson);

    private static string NewSubject() => $"customer-{Guid.NewGuid():N}";

    private static CartView View(long stamp, int version = 1, int quantity = 1)
    {
        var now = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
        return new CartView(
            Guid.Parse("0193b6b5-6b9e-7c3a-9d58-3c1f5b2a7e10"),
            version,
            stamp.ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
            Guid.Parse("0193b6a0-11aa-7bbb-8ccc-0d0e0f101112"),
            "EUR",
            [
                new CartLineView(
                    Guid.Parse("0193b6b5-7000-7a00-8b00-000000000001"),
                    Guid.Parse("0193b6a2-2222-7333-8444-555566667777"),
                    Guid.Parse("0193b6a2-2222-7333-8444-555566668888"),
                    Guid.Parse("0193b6a0-11aa-7bbb-8ccc-0d0e0f101112"),
                    "Blue shirt",
                    "BS-M",
                    [new VariantOption("size", "M")],
                    "https://cdn.example.com/a.jpg",
                    "Front",
                    quantity,
                    19.99m,
                    "EUR",
                    ItemAvailability.Available,
                    now,
                    now,
                    now),
            ],
            now,
            now);
    }
}
