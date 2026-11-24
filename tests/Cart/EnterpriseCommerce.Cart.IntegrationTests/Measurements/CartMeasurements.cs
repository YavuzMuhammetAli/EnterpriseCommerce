using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using EnterpriseCommerce.Cart.Infrastructure.Caching;
using EnterpriseCommerce.Cart.IntegrationTests.Caching;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EnterpriseCommerce.Cart.IntegrationTests.Measurements;

[CollectionDefinition(nameof(MeasurementCollection), DisableParallelization = true)]
public sealed class MeasurementCollection;

/// <summary>
/// Opt-in local measurements (<c>EC_TEST_MEASURE=true</c>; never in CI), reported in PHASE_6_CART.md section 11. Each
/// scenario prints one markdown row: concurrency, operations, elapsed time, throughput, p50, p95 and p99 per request,
/// and refusals. The host runs in-process (no HTTP network), against the Compose SQL Server and cache Redis, with the
/// in-process Catalog stub. They are observations of this machine, never targets or production expectations.
/// </summary>
[Collection(nameof(MeasurementCollection))]
public sealed class CartMeasurements(CartCacheApiFixture fixture) : IClassFixture<CartCacheApiFixture>
{
    private const string Variable = "EC_TEST_MEASURE";
    private const string OutputVariable = "EC_TEST_MEASURE_OUTPUT";

    [Fact]
    public async Task ReadsWithACacheHit()
    {
        RequireEnabled();
        var customers = await SeedCartsAsync(16, lines: 5);
        await ReadAllAsync(fixture.Factory, customers);

        await MeasureAsync("GET, cache hit (5-line carts)", fixture.Factory, customers, operations: 1600, (client, customer, _) => client.GetAsync("/api/v1/cart"));
    }

    [Fact]
    public async Task ReadsWithACacheMiss()
    {
        RequireEnabled();
        var customers = await SeedCartsAsync(16, lines: 5);

        await MeasureAsync(
            "GET, cache miss (key deleted before each read; SQL Server read and repopulation)",
            fixture.Factory,
            customers,
            operations: 1600,
            (client, customer, _) => client.GetAsync("/api/v1/cart"),
            before: customer => fixture.Redis.Database.KeyDeleteAsync(RedisCartCache.KeyFor(fixture.Redis.KeyPrefix, customer)));
    }

    [Fact]
    public async Task ReadsWithRedisUnavailable()
    {
        RequireEnabled();
        var customers = await SeedCartsAsync(16, lines: 5);
        await using var factory = fixture.CreateFactory(builder => builder.UseSetting("ConnectionStrings:CartCache", RedisTestServer.ConnectionString(RedisTestServer.ClosedPort())));

        await MeasureAsync("GET, Redis unreachable (SQL Server only)", factory, customers, operations: 1600, (client, customer, _) => client.GetAsync("/api/v1/cart"));
    }

    [Fact]
    public async Task AddsAndQuantityChanges_OnDifferentCarts()
    {
        RequireEnabled();
        var merchant = Guid.CreateVersion7();
        var variants = Enumerable.Range(0, 50).Select(_ => StubVariant.New()).ToArray();
        var product = fixture.Catalog.Publish(merchant, variants);
        var customers = Enumerable.Range(0, 16).Select(_ => CartApiFixture.NewCustomer()).ToArray();
        var lines = new System.Collections.Concurrent.ConcurrentDictionary<string, Guid>();

        await MeasureAsync(
            "POST item (new line, Catalog stub read included), 16 different carts",
            fixture.Factory,
            customers,
            operations: 640,
            async (client, customer, index) =>
            {
                var response = await client.PostAsJsonAsync("/api/v1/cart/items", new { productId = product, variantId = variants[index % 40].Id, quantity = 1 });
                if (response.StatusCode == HttpStatusCode.Created)
                {
                    lines.TryAdd(customer, Guid.Parse(response.Headers.Location!.OriginalString.Split('/')[^1]));
                }

                return response;
            });

        await MeasureAsync(
            "PUT quantity, 16 different carts",
            fixture.Factory,
            customers,
            operations: 640,
            (client, customer, index) => client.PutAsJsonAsync($"/api/v1/cart/items/{lines[customer]}", new { quantity = 1 + (index % 98) }));
    }

    [Fact]
    public async Task ConcurrentAdds_OnOneCart_AndOnDifferentCarts()
    {
        RequireEnabled();
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
        var shared = CartApiFixture.NewCustomer();

        await MeasureAsync(
            "POST same variant, one cart, 16 callers (contended)",
            fixture.Factory,
            Enumerable.Repeat(shared, 16).ToArray(),
            operations: 96,
            (client, _, _) => client.PostAsJsonAsync("/api/v1/cart/items", new { productId = product, variantId = variant.Id, quantity = 1 }));

        var pair = CartApiFixture.NewCustomer();
        await MeasureAsync(
            "POST same variant, one cart, 4 callers (two tabs and a double click)",
            fixture.Factory,
            Enumerable.Repeat(pair, 4).ToArray(),
            operations: 96,
            (client, _, _) => client.PostAsJsonAsync("/api/v1/cart/items", new { productId = product, variantId = variant.Id, quantity = 1 }));

        var others = Enumerable.Range(0, 16).Select(_ => CartApiFixture.NewCustomer()).ToArray();
        await MeasureAsync(
            "POST same variant, 16 carts, 16 callers (independent)",
            fixture.Factory,
            others,
            operations: 1584,
            (client, _, _) => client.PostAsJsonAsync("/api/v1/cart/items", new { productId = product, variantId = variant.Id, quantity = 1 }));
    }

    private static void RequireEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(Variable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip($"Measurement: set {Variable}=true to run it (local only).");
        }

        RedisTestServer.RequireAvailable();
    }

    private async Task<string[]> SeedCartsAsync(int count, int lines)
    {
        fixture.RequireAvailable();
        var variants = Enumerable.Range(0, lines).Select(_ => StubVariant.New()).ToArray();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variants);
        var customers = Enumerable.Range(0, count).Select(_ => CartApiFixture.NewCustomer()).ToArray();
        foreach (var customer in customers)
        {
            foreach (var variant in variants)
            {
                await fixture.AddAsync(customer, product, variant.Id, 2);
            }
        }

        return customers;
    }

    private async Task ReadAllAsync(WebApplicationFactory<Program> factory, string[] customers)
    {
        foreach (var customer in customers)
        {
            using var client = CartApiFixture.CreateClient(factory, fixture.CustomerToken(customer));
            using var response = await client.GetAsync("/api/v1/cart", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    /// <summary>
    /// Runs <paramref name="operations"/> requests over one caller per entry of <paramref name="customers"/>, all at
    /// once, and reports the row. A <c>409</c> is counted as a refusal; any other non-success fails the measurement.
    /// </summary>
    private async Task MeasureAsync(
        string scenario,
        WebApplicationFactory<Program> factory,
        string[] customers,
        int operations,
        Func<HttpClient, string, int, Task<HttpResponseMessage>> request,
        Func<string, Task>? before = null)
    {
        var callers = customers.Length;
        var perCaller = operations / callers;
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var refused = 0;
        var clients = customers.Select(customer => CartApiFixture.CreateClient(factory, fixture.CustomerToken(customer))).ToArray();

        // Warm-up: one request per caller, not measured.
        for (var caller = 0; caller < callers; caller++)
        {
            using var warm = await clients[caller].GetAsync("/api/v1/cart", TestContext.Current.CancellationToken);
        }

        // An asynchronous start gate: a blocking barrier would hold thread-pool threads, and the time the pool takes to
        // inject more would be measured as elapsed time (finding F3).
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Enumerable.Range(0, callers).Select(caller => Task.Run(async () =>
        {
            await start.Task;
            for (var index = 0; index < perCaller; index++)
            {
                if (before is not null)
                {
                    await before(customers[caller]);
                }

                var watch = Stopwatch.StartNew();
                using var response = await request(clients[caller], customers[caller], index);
                watch.Stop();
                latencies.Add(watch.Elapsed.TotalMilliseconds);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    Interlocked.Increment(ref refused);
                }
                else
                {
                    Assert.True(response.IsSuccessStatusCode, $"{scenario}: {response.StatusCode}");
                }
            }
        })).ToList();
        var total = Stopwatch.StartNew();
        start.SetResult();
        await Task.WhenAll(running);
        total.Stop();

        foreach (var client in clients)
        {
            client.Dispose();
        }

        var sorted = latencies.Order().ToList();
        var row = string.Create(
            CultureInfo.InvariantCulture,
            $"| {scenario} | {callers} | {sorted.Count} | {total.Elapsed.TotalSeconds:0.00} s | {sorted.Count / total.Elapsed.TotalSeconds:0}/s | {Percentile(sorted, 0.50):0.0} | {Percentile(sorted, 0.95):0.0} | {Percentile(sorted, 0.99):0.0} | {refused} |");
        TestContext.Current.TestOutputHelper?.WriteLine(row);
        if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } output)
        {
            await File.AppendAllTextAsync(output, row + Environment.NewLine, TestContext.Current.CancellationToken);
        }
    }

    private static double Percentile(List<double> sorted, double p) =>
        sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}
