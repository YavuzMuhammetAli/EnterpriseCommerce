using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Measurements;

/// <summary>
/// Local measurements (PHASE_5_INVENTORY.md, measurements): opt-in with <c>EC_TEST_MEASURE=true</c>, never part of a
/// normal run. They report what one developer machine measured, through the in-memory test server against the Compose
/// SQL Server and Kafka; they set no target and guarantee nothing. Results are written to <c>EC_TEST_MEASURE_OUTPUT</c>
/// (a file path) when set, and every run still asserts correctness.
/// </summary>
public sealed class InventoryMeasurements : IAsyncLifetime
{
    private const string EnabledVariable = "EC_TEST_MEASURE";
    private const string OutputVariable = "EC_TEST_MEASURE_OUTPUT";

    private readonly InventoryApiFixture _fixture = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _fixture.InitializeAsync();

    public async ValueTask DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task ReservationsOfOneItem_ContendOnItsRowLock()
    {
        RequireEnabled();
        var tenantId = _fixture.NewTenant(out var owner, out _, out _);
        var item = await _fixture.CreateItemAsync(tenantId, owner, onHand: 100_000);
        const int Callers = 32;
        const int PerCaller = 25;
        await WarmUpAsync(tenantId, item);

        var (elapsed, latencies) = await RunAsync(Callers, PerCaller, _ => ReserveAsync(tenantId, [Line(item, 1)]));

        Assert.Equal(Callers * PerCaller + 1, await ReservedAsync(item));
        Report("reservations, one item (contended)", Callers, Callers * PerCaller, elapsed, latencies);
    }

    [Fact]
    public async Task ReservationsOfIndependentItems_DoNotContend()
    {
        RequireEnabled();
        var tenantId = _fixture.NewTenant(out var owner, out _, out _);
        const int Callers = 32;
        const int PerCaller = 25;
        var items = new List<JsonElement>();
        for (var i = 0; i < Callers; i++)
        {
            items.Add(await _fixture.CreateItemAsync(tenantId, owner, onHand: 1_000));
        }

        await WarmUpAsync(tenantId, items[0]);

        var (elapsed, latencies) = await RunAsync(Callers, PerCaller, caller => ReserveAsync(tenantId, [Line(items[caller], 1)]));

        Assert.Equal(PerCaller, await ReservedAsync(items[1]));
        Report("reservations, one item per caller (independent)", Callers, Callers * PerCaller, elapsed, latencies);
    }

    [Fact]
    public async Task TenLineReservations_LockTenItemsInOrder()
    {
        RequireEnabled();
        var tenantId = _fixture.NewTenant(out var owner, out _, out _);
        var items = new List<JsonElement>();
        for (var i = 0; i < 20; i++)
        {
            items.Add(await _fixture.CreateItemAsync(tenantId, owner, onHand: 10_000));
        }

        await WarmUpAsync(tenantId, items[0]);
        const int Callers = 16;
        const int PerCaller = 20;

        // Overlapping sets: each caller takes 10 of the 20 items, so callers contend on some rows. The queue can exceed
        // the 5-second lock timeout: such a request is refused with 503 (fail closed, ADR-026 section 9) and counted.
        var (waitsBefore, deadlocksBefore, timeoutsBefore) = (await LockWaitsAsync(), DeadlockRetries(), LockTimeouts());
        var refused = 0;
        var (elapsed, latencies) = await RunAsync(Callers, PerCaller, async caller =>
        {
            var status = await ReserveOrRefuseAsync(tenantId, [.. Enumerable.Range(0, 10).Select(offset => Line(items[(caller + offset) % items.Count], 1))]);
            if (status == HttpStatusCode.ServiceUnavailable)
            {
                Interlocked.Increment(ref refused);
            }

            return status;
        });
        var waits = await LockWaitsAsync();

        // Every refusal was a lock timeout, and only the accepted reservations hold stock.
        Assert.Equal(refused, LockTimeouts() - timeoutsBefore);
        Assert.Equal(((Callers * PerCaller) - refused) * 10 + 1, (await Task.WhenAll(items.Select(ReservedAsync))).Sum());
        Report(
            $"10-line reservations over 20 shared items (refused at the 5 s lock timeout: {refused}; lock waits: {waits.Count - waitsBefore.Count}, {waits.Milliseconds - waitsBefore.Milliseconds} ms in total; deadlock retries: {DeadlockRetries() - deadlocksBefore})",
            Callers, Callers * PerCaller, elapsed, latencies);
    }

    [Fact]
    public async Task TenLineReservations_OfDisjointItems_ShowTheRoundTripCost()
    {
        RequireEnabled();
        var tenantId = _fixture.NewTenant(out var owner, out _, out _);
        const int Callers = 16;
        const int PerCaller = 20;
        var items = new List<JsonElement>();
        for (var i = 0; i < Callers * 10; i++)
        {
            items.Add(await _fixture.CreateItemAsync(tenantId, owner, onHand: 10_000));
        }

        await WarmUpAsync(tenantId, items[0]);

        // Every caller has 10 items of its own: no two callers ever wait for the same row.
        var (waitsBefore, deadlocksBefore) = (await LockWaitsAsync(), DeadlockRetries());
        var (elapsed, latencies) = await RunAsync(Callers, PerCaller, caller => ReserveAsync(tenantId, [.. Enumerable.Range(0, 10).Select(offset => Line(items[(caller * 10) + offset], 1))]));
        var waits = await LockWaitsAsync();

        Assert.Equal(Callers * PerCaller * 10 + 1, (await Task.WhenAll(items.Select(ReservedAsync))).Sum());
        Report(
            $"10-line reservations of disjoint items (lock waits: {waits.Count - waitsBefore.Count}, {waits.Milliseconds - waitsBefore.Milliseconds} ms in total; deadlock retries: {DeadlockRetries() - deadlocksBefore})",
            Callers, Callers * PerCaller, elapsed, latencies);
    }

    /// <summary>SQL Server's cumulative lock waits (every LCK_M_* type), server-wide: run measurements alone.</summary>
    private async Task<(long Count, long Milliseconds)> LockWaitsAsync()
    {
        var text = await _fixture.Database.ScalarAsync<string>("SELECT CONCAT(SUM(waiting_tasks_count), '|', SUM(wait_time_ms)) FROM sys.dm_os_wait_stats WHERE wait_type LIKE 'LCK_M_%'");
        var parts = text.Split('|');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture), long.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Requests refused because a lock wait exceeded <c>LOCK_TIMEOUT</c> (SQL Server error 1222): the exception handler's
    /// event for the 503, whose cause is that error. EF Core logs the same SQL error once more, so only this event counts.
    /// </summary>
    private int LockTimeouts() => _fixture.Logs.Events.Count(logEvent =>
        logEvent.Exception is DataStoreUnavailableException && IsLockTimeout(logEvent.Exception));

    private static bool IsLockTimeout(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 1222 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whole-transaction retries of the write gate after a deadlock (log event 5020).</summary>
    private int DeadlockRetries() => _fixture.Logs.Events.Count(logEvent => logEvent.MessageTemplate.Text.Contains("deadlock victim", StringComparison.Ordinal));

    [Fact]
    public async Task ListingAndMovementPaging_OnALargeTenant()
    {
        RequireEnabled();
        var tenantId = _fixture.NewTenant(out var owner, out _, out _);
        var item = await _fixture.CreateItemAsync(tenantId, owner, onHand: 0);
        await SeedLargeTenantAsync(tenantId, items: 10_000);
        await SeedLongLedgerAsync(item, movements: 100_000);
        var token = _fixture.UserToken(owner);
        var itemId = item.GetProperty("id").GetGuid();

        foreach (var (name, path) in new[]
        {
            ("item list, first page (20)", $"/api/v1/inventory/tenants/{tenantId}/items"),
            ("item list, page 100 of 20", $"/api/v1/inventory/tenants/{tenantId}/items?page=100"),
            ("item list, last page of 100", $"/api/v1/inventory/tenants/{tenantId}/items?page=100&pageSize=100"),
            ("item list, SKU filter", $"/api/v1/inventory/tenants/{tenantId}/items?sku=BULK-0005000"),
            ("movements, newest page (20)", $"/api/v1/inventory/tenants/{tenantId}/items/{itemId}/movements"),
            ("movements, page 5000 of 20", $"/api/v1/inventory/tenants/{tenantId}/items/{itemId}/movements?page=5000"),
        })
        {
            await GetAsync(token, path);
            var latencies = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var started = Stopwatch.GetTimestamp();
                await GetAsync(token, path);
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            Report($"{name}; 10,001 items, 100,001 movements", 1, latencies.Count, TimeSpan.FromMilliseconds(latencies.Sum()), latencies);
        }
    }

    [Fact]
    public async Task TheOutboxRelay_DrainsABacklog()
    {
        RequireEnabled();
        KafkaTestEnvironment.RequireAvailable();
        await using var topic = await KafkaTestEnvironment.CreateTopicAsync();
        var tenantId = _fixture.NewTenant(out var owner, out _, out _);
        await _fixture.CreateItemAsync(tenantId, owner, onHand: 1);
        const int Rows = 5_000;

        // A backlog of real envelopes, copied with new event IDs (the relay publishes bytes; it does not parse payloads).
        await _fixture.Database.ExecuteAsync($"UPDATE OutboxMessages SET Topic = '{topic.Name}'");
        await _fixture.Database.ExecuteAsync(
            $"INSERT INTO OutboxMessages (EventId, Topic, MessageKey, EventType, EventVersion, Envelope, OccurredAt) "
            + $"SELECT NEWID(), Topic, MessageKey, EventType, EventVersion, Envelope, OccurredAt FROM OutboxMessages CROSS JOIN (SELECT TOP ({Rows - 1}) 1 AS n FROM sys.all_objects a CROSS JOIN sys.all_objects b) AS copies");

        var started = Stopwatch.GetTimestamp();
        await using var host = _fixture.CreateFactory(builder =>
        {
            builder.UseSetting("Inventory:Kafka:BootstrapServers", KafkaTestEnvironment.BootstrapServers);
            builder.UseSetting("Inventory:Outbox:Topic", topic.Name);
            builder.UseSetting("Inventory:Outbox:PollInterval", "00:00:00.100");
        });
        using (host.CreateClient())
        {
        }

        await Eventually.TrueAsync(async () => await _fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM OutboxMessages WHERE PublishedAt IS NULL") == 0, TimeSpan.FromMinutes(10), "the backlog is drained");
        var elapsed = Stopwatch.GetElapsedTime(started);

        Report("outbox relay drain, one leader, sequential acknowledged publishing", 1, Rows, elapsed, []);
    }

    private static void RequireEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip($"A local measurement: set {EnabledVariable}=true to run it.");
        }
    }

    private async Task WarmUpAsync(Guid tenantId, JsonElement item) => Assert.Equal(HttpStatusCode.Created, await ReserveAsync(tenantId, [Line(item, 1)]));

    private async Task<HttpStatusCode> ReserveAsync(Guid tenantId, object[] lines)
    {
        using var response = await _fixture.SendAsync(_fixture.ServiceToken(), HttpMethod.Post, $"/internal/v1/inventory/tenants/{tenantId}/reservations", new { lines }, InventoryApiFixture.Key());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return response.StatusCode;
    }

    /// <summary>A reservation that is either created or refused with 503; anything else fails the measurement.</summary>
    private async Task<HttpStatusCode> ReserveOrRefuseAsync(Guid tenantId, object[] lines)
    {
        using var response = await _fixture.SendAsync(_fixture.ServiceToken(), HttpMethod.Post, $"/internal/v1/inventory/tenants/{tenantId}/reservations", new { lines }, InventoryApiFixture.Key());
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync(Token));
        return response.StatusCode;
    }

    private async Task GetAsync(string token, string path)
    {
        using var response = await _fixture.SendAsync(token, HttpMethod.Get, path);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
    }

    private static async Task<(TimeSpan Elapsed, List<double> Latencies)> RunAsync(int callers, int perCaller, Func<int, Task<HttpStatusCode>> call)
    {
        using var start = new Barrier(callers);
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, callers).Select(caller => Task.Run(
            async () =>
            {
                start.SignalAndWait(TimeSpan.FromSeconds(30));
                for (var i = 0; i < perCaller; i++)
                {
                    var began = Stopwatch.GetTimestamp();
                    await call(caller);
                    latencies.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
                }
            },
            Token)));
        return (Stopwatch.GetElapsedTime(started), [.. latencies]);
    }

    private Task<int> ReservedAsync(JsonElement item) =>
        _fixture.Database.ScalarAsync<int>($"SELECT Reserved FROM InventoryItems WHERE Id = '{item.GetProperty("id").GetGuid()}'");

    /// <summary>Set-based rows that satisfy every constraint: items with their initial stock movement.</summary>
    private Task SeedLargeTenantAsync(Guid tenantId, int items) =>
        _fixture.Database.ExecuteAsync(
            $"""
            WITH n AS (SELECT TOP ({items}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO InventoryItems (Id, TenantId, ProductId, VariantId, Sku, OnHand, Reserved, Version, CreatedAt, UpdatedAt, CreatedBySubject, CreationKey, CreationRequestHash)
            SELECT NEWID(), '{tenantId}', NEWID(), NEWID(), CONCAT('BULK-', RIGHT(CONCAT('0000000', i), 7)), 10, 0, 1, DATEADD(MILLISECOND, i, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET(), 'bulk', CONCAT('bulk-', i), 0x{new string('0', 64)} FROM n;
            INSERT INTO StockMovements (Id, TenantId, InventoryItemId, ItemVersion, Type, OnHandDelta, ReservedDelta, OnHandBefore, ReservedBefore, OnHandAfter, ReservedAfter, ActorKind, ActorId, CorrelationId, OccurredAt)
            SELECT NEWID(), TenantId, Id, 1, 'InitialStock', 10, 0, 0, 0, 10, 0, 'User', 'bulk', 'bulk', CreatedAt FROM InventoryItems WHERE TenantId = '{tenantId}' AND CreatedBySubject = 'bulk';
            """);

    /// <summary>A linear ledger of receipts on one item, and the item's matching balance.</summary>
    private Task SeedLongLedgerAsync(JsonElement item, int movements) =>
        _fixture.Database.ExecuteAsync(
            $"""
            DECLARE @item uniqueidentifier = '{item.GetProperty("id").GetGuid()}';
            DECLARE @tenant uniqueidentifier = (SELECT TenantId FROM InventoryItems WHERE Id = @item);
            WITH n AS (SELECT TOP ({movements}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO StockMovements (Id, TenantId, InventoryItemId, ItemVersion, Type, Reason, OnHandDelta, ReservedDelta, OnHandBefore, ReservedBefore, OnHandAfter, ReservedAfter, ActorKind, ActorId, CorrelationId, OccurredAt, IdempotencyKey, RequestHash)
            SELECT NEWID(), @tenant, @item, i + 1, 'Adjustment', 'Receipt', 1, 0, i - 1, 0, i, 0, 'User', 'bulk', 'bulk', SYSDATETIMEOFFSET(), CONCAT('bulk-ledger-', i), 0x{new string('0', 64)} FROM n;
            UPDATE InventoryItems SET OnHand = {movements}, Version = {movements + 1} WHERE Id = @item;
            """);

    private static object Line(JsonElement item, int quantity) => new { variantId = item.GetProperty("variantId").GetGuid(), quantity };

    private static void Report(string scenario, int concurrency, int operations, TimeSpan elapsed, List<double> latencies)
    {
        latencies.Sort();
        string Percentile(double p) => latencies.Count == 0 ? "-" : latencies[Math.Clamp((int)Math.Ceiling(p * latencies.Count) - 1, 0, latencies.Count - 1)].ToString("0.0", CultureInfo.InvariantCulture);
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"| {scenario} | {concurrency} | {operations} | {elapsed.TotalSeconds:0.00} s | {operations / elapsed.TotalSeconds:0.0}/s | {Percentile(0.5)} | {Percentile(0.95)} | {Percentile(0.99)} |");
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        if (Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } path)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
