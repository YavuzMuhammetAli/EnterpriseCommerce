using System.Diagnostics;
using System.Net;
using EnterpriseCommerce.Cart.Infrastructure.Caching;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Cart.IntegrationTests.Caching;

[CollectionDefinition(nameof(CacheRestartCollection), DisableParallelization = true)]
public sealed class CacheRestartCollection;

/// <summary>
/// A real restart of the cache Redis container (opt-in: <c>EC_TEST_REDIS_CACHE_RESTART_CONTAINER</c> names it, for
/// example <c>enterprisecommerce-redis-1</c>; CI sets it). Requests keep working while Redis is down, and the entry
/// that Redis reloads from its own snapshot after the restart, older than the cart committed during the outage, is
/// replaced by the repair rather than served (ADR-030 section 4). It runs alone: it stops a shared server.
/// </summary>
[Collection(nameof(CacheRestartCollection))]
public sealed class CacheRestartTests(CartCacheApiFixture fixture) : IClassFixture<CartCacheApiFixture>
{
    public const string ContainerVariable = "EC_TEST_REDIS_CACHE_RESTART_CONTAINER";

    [Fact]
    public async Task ARedisRestart_NeverFailsARequest_AndTheCartCommittedMeanwhileWins()
    {
        fixture.RequireAvailable();
        var container = Environment.GetEnvironmentVariable(ContainerVariable);
        if (string.IsNullOrWhiteSpace(container))
        {
            Infra.SkipOrFail($"Redis restart test: set {ContainerVariable} to the cache Redis container name.");
        }

        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
        var cart = await fixture.AddAsync(customer, product, variant.Id);
        var line = cart.GetProperty("items")[0].GetProperty("id").GetGuid();
        await fixture.GetCartAsync(customer);

        // Persist the cached version 1 into Redis's own snapshot, so the restarted server reloads it: a stale entry.
        await fixture.Redis.Database.ExecuteAsync("SAVE");
        await Docker("stop", container!);
        try
        {
            using var set = await fixture.SendAsync(customer, HttpMethod.Put, $"/api/v1/cart/items/{line}", new { quantity = 7 });
            Assert.Equal(HttpStatusCode.OK, set.StatusCode);
            Assert.Equal(7, (await fixture.GetCartAsync(customer)).GetProperty("items")[0].GetProperty("quantity").GetInt32());
        }
        finally
        {
            await Docker("start", container!);
        }

        await Eventually.TrueAsync(
            async () =>
            {
                try
                {
                    return (string?)await fixture.Redis.Database.HashGetAsync(RedisCartCache.KeyFor(fixture.Redis.KeyPrefix, customer), "rv") is { } stamp
                        && stamp == await StoredStampAsync(customer);
                }
                catch (StackExchange.Redis.RedisException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(60),
            "the repair replaced the reloaded entry with the committed cart");

        Assert.Equal(7, (await fixture.GetCartAsync(customer)).GetProperty("items")[0].GetProperty("quantity").GetInt32());
    }

    private async Task<string> StoredStampAsync(string customer) =>
        Convert.ToHexString(await fixture.Database.ScalarAsync<byte[]>($"SELECT RowVersion FROM Carts WHERE CustomerSubject = N'{customer}'"));

    private static async Task Docker(string command, string container)
    {
        using var process = Process.Start(new ProcessStartInfo("docker", [command, container]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"docker {command} {container} failed: {await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken)}");
    }
}
