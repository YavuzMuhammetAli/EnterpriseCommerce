using System.Data;
using System.Net;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Reservations;
using EnterpriseCommerce.Inventory.Infrastructure.Reservations;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Inventory.UnitTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Reservations;

/// <summary>
/// Reservation expiry on a real SQL Server (ADR-026 section 7): the worker's effects, several instances at once, its
/// race with commit and release, recovery after an interrupted claim, the hosted worker and its health check.
/// </summary>
public sealed class ReservationExpiryTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private SqlServerTestDatabase Database => fixture.Database;

    [Fact]
    public async Task AnOverdueReservation_Expires_ReturningItsStock_WithItsMovementsAndEvents()
    {
        fixture.RequireAvailable();
        var tenant = Guid.CreateVersion7();
        var first = await InventorySeed.CreateItemAsync(Database, tenant, onHand: 5);
        var second = await InventorySeed.CreateItemAsync(Database, tenant, onHand: 5);
        var overdue = await InventorySeed.ReserveAsync(Database, tenant, [first, second], quantity: 2);
        var due = await InventorySeed.ReserveAsync(Database, tenant, [first], quantity: 1);
        await InventorySeed.MakeOverdueAsync(Database, overdue);

        var expired = await Runner().RunOnceAsync(Token);

        Assert.True(expired >= 1);
        Assert.Equal(("Expired", "Active"), (await StatusAsync(overdue), await StatusAsync(due)));
        Assert.Equal("5|1", await BalanceAsync(first.Id));
        Assert.Equal("5|0", await BalanceAsync(second.Id));
        Assert.Equal(2, await Database.ScalarAsync<int>($"SELECT COUNT(*) FROM StockMovements WHERE ReservationId = '{overdue}' AND Type = 'ReservationExpiry' AND ActorKind = 'System' AND ActorId = 'reservation-expiry'"));
        var envelope = await Database.ScalarAsync<string>($"SELECT Envelope FROM OutboxMessages WHERE EventType = 'ReservationExpired' AND Envelope LIKE '%{overdue}%'");
        EventSchemas.AssertValid(envelope);
        Assert.Contains($"\"correlationId\":\"reservation-expiry-{overdue:N}\"", envelope, StringComparison.Ordinal);
        Assert.Equal(0, await Runner().RunOnceAsync(Token) - await CountOtherOverdueAsync());
    }

    [Fact]
    public async Task TwoInstances_ExpireEachOverdueReservationExactlyOnce()
    {
        fixture.RequireAvailable();
        const int Reservations = 200;
        var tenant = Guid.CreateVersion7();
        var items = new List<Domain.Items.InventoryItem>();
        for (var i = 0; i < 20; i++)
        {
            items.Add(await InventorySeed.CreateItemAsync(Database, tenant, onHand: 100));
        }

        var ids = new List<Guid>();
        for (var i = 0; i < Reservations; i++)
        {
            ids.Add(await InventorySeed.ReserveAsync(Database, tenant, [items[i % items.Count], items[(i + 7) % items.Count]]));
        }

        await Database.ExecuteAsync($"UPDATE Reservations SET CreatedAt = DATEADD(MINUTE, -30, SYSUTCDATETIME()), ExpiresAt = DATEADD(MINUTE, -15, SYSUTCDATETIME()) WHERE TenantId = '{tenant}'");

        // Two workers, each draining with small batches, as two service instances would.
        var counts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            var runner = Runner(batchSize: 10);
            var total = 0;
            int expired;
            do
            {
                expired = await runner.RunOnceAsync(Token);
                total += expired;
            }
            while (expired > 0);

            return total;
        })));

        Assert.True(counts.Sum() >= Reservations, $"The workers expired {counts.Sum()} reservations.");
        Assert.Equal(Reservations, await Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Reservations WHERE TenantId = '{tenant}' AND Status = 'Expired'"));
        Assert.Equal(Reservations, await Database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE MessageKey = '{tenant}' AND EventType = 'ReservationExpired'"));
        Assert.Equal(Reservations * 2, await Database.ScalarAsync<int>($"SELECT COUNT(*) FROM StockMovements WHERE TenantId = '{tenant}' AND Type = 'ReservationExpiry'"));
        Assert.Equal(0, await Database.ScalarAsync<int>($"SELECT COUNT(*) FROM InventoryItems WHERE TenantId = '{tenant}' AND (Reserved <> 0 OR OnHand <> 100)"));
    }

    [Fact]
    public async Task ExpiryCommitAndRelease_Racing_TransitionAReservationExactlyOnce()
    {
        fixture.RequireAvailable();
        for (var round = 0; round < 15; round++)
        {
            var tenant = Guid.CreateVersion7();
            var item = await InventorySeed.CreateItemAsync(Database, tenant, onHand: 10);
            var id = await InventorySeed.ReserveAsync(Database, tenant, [item], quantity: 4);
            await InventorySeed.MakeOverdueAsync(Database, id);
            using var start = new Barrier(3);

            await Task.WhenAll(
                Task.Run(async () => { start.SignalAndWait(TimeSpan.FromSeconds(30)); await Runner().RunOnceAsync(Token); }, Token),
                Task.Run(async () => { start.SignalAndWait(TimeSpan.FromSeconds(30)); await TransitionAsync(tenant, id, commit: true); }, Token),
                Task.Run(async () => { start.SignalAndWait(TimeSpan.FromSeconds(30)); await TransitionAsync(tenant, id, commit: false); }, Token));

            // Overdue, so neither commit nor release can win: exactly one expiry, by whichever transaction locked first.
            Assert.Equal("Expired", await StatusAsync(id));
            Assert.Equal("10|0", await BalanceAsync(item.Id));
            Assert.Equal(1, await Database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE MessageKey = '{tenant}' AND EventType IN ('ReservationExpired', 'ReservationCommitted', 'ReservationReleased')"));
            Assert.Equal(1, await Database.ScalarAsync<int>($"SELECT COUNT(*) FROM StockMovements WHERE ReservationId = '{id}' AND Type <> 'Reservation'"));
        }
    }

    [Fact]
    public async Task AClaimHeldByAnInterruptedTransaction_IsSkipped_AndExpiredOnceItIsReleased()
    {
        fixture.RequireAvailable();
        var tenant = Guid.CreateVersion7();
        var item = await InventorySeed.CreateItemAsync(Database, tenant, onHand: 10);
        var held = await InventorySeed.ReserveAsync(Database, tenant, [item]);
        var free = await InventorySeed.ReserveAsync(Database, tenant, [item]);
        await InventorySeed.MakeOverdueAsync(Database, held);
        await InventorySeed.MakeOverdueAsync(Database, free);

        // Another instance claimed one and stopped mid-transaction (a crash before its commit).
        await using (var connection = new SqlConnection(Database.ConnectionString))
        {
            await connection.OpenAsync(Token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, Token);
            await using (var claim = new SqlCommand($"SELECT Id FROM Reservations WITH (UPDLOCK, ROWLOCK) WHERE Id = '{held}'", connection, transaction))
            {
                await claim.ExecuteScalarAsync(Token);
            }

            await Runner().RunOnceAsync(Token);
            Assert.Equal(("Active", "Expired"), (await StatusAsync(held), await StatusAsync(free)));

            // The crashed transaction rolls back when its connection goes away.
            await transaction.RollbackAsync(Token);
        }

        await Runner().RunOnceAsync(Token);
        Assert.Equal("Expired", await StatusAsync(held));
        Assert.Equal("10|0", await BalanceAsync(item.Id));
    }

    [Fact]
    public async Task TheHostedWorker_ExpiresOverdueReservations_AndTheHealthCheckReportsWhatStaysOverdue()
    {
        fixture.RequireAvailable();
        var tenant = Guid.CreateVersion7();
        var item = await InventorySeed.CreateItemAsync(Database, tenant, onHand: 10);
        var id = await InventorySeed.ReserveAsync(Database, tenant, [item]);
        await InventorySeed.MakeOverdueAsync(Database, id);

        // Without the worker the reservation stays overdue: the expiry check reports it, and readiness is unaffected.
        using (var idle = fixture.CreateClient())
        {
            Assert.Equal("Degraded", await idle.GetStringAsync("/health/expiry", Token));
            Assert.Equal("Healthy", await idle.GetStringAsync("/health/ready", Token));
        }

        await using var running = fixture.CreateFactory(builder =>
        {
            builder.UseSetting("Inventory:ReservationExpiry:Enabled", "true");
            builder.UseSetting("Inventory:ReservationExpiry:Interval", "00:00:01");
        });
        using var client = InventoryApiFixture.CreateClient(running, null);
        await client.GetAsync("/health/live", Token);

        await Eventually.TrueAsync(async () => await StatusAsync(id) == "Expired", TimeSpan.FromSeconds(30), "the hosted worker expires the reservation");
        await Eventually.TrueAsync(async () => await client.GetStringAsync("/health/expiry", Token) == "Healthy", TimeSpan.FromSeconds(30), "the expiry check is healthy again");
        Assert.Equal("10|0", await BalanceAsync(item.Id));
    }

    [Theory]
    [InlineData("Interval", "00:00:00.500")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "501")]
    [InlineData("OverdueAfter", "00:00:05")]
    public void InvalidWorkerSettings_AreRefused(string setting, string value)
    {
        var options = new ReservationExpiryOptions();
        switch (setting)
        {
            case "Interval": options.Interval = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "BatchSize": options.BatchSize = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            default: options.OverdueAfter = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
        }

        Assert.NotEmpty(options.Validate());
        Assert.Empty(new ReservationExpiryOptions().Validate());
    }

    private ReservationExpiryRunner Runner(int batchSize = 50) =>
        new(Database.Gate(), Options.Create(new ReservationExpiryOptions { BatchSize = batchSize }), NullLogger<ReservationExpiryRunner>.Instance);

    private Task<TransitionResult> TransitionAsync(Guid tenant, Guid id, bool commit) =>
        Database.Gate().ExecuteAsync(
            "test-transition",
            async (session, cancellationToken) =>
            {
                var reservation = (await session.LockReservationAsync(tenant, id, cancellationToken))!;
                var items = reservation.IsTerminal
                    ? new Dictionary<Guid, Domain.Items.InventoryItem>()
                    : await session.LockItemsAsync(tenant, [.. reservation.Lines.Select(line => line.InventoryItemId)], cancellationToken);
                var actor = Actor.Service("svc-test-order");
                var result = commit ? reservation.Commit(items, actor, "corr-race", session.Now) : reservation.Release(items, actor, "corr-race", session.Now);
                session.UseMessageMetadata(InventorySeed.Metadata);
                return result.Outcome is TransitionOutcome.Changed or TransitionOutcome.ExpiredNow ? WriteDecision.Commit(result) : WriteDecision.Rollback(result);
            },
            Token);

    private Task<string> StatusAsync(Guid id) => Database.ScalarAsync<string>($"SELECT Status FROM Reservations WHERE Id = '{id}'");

    private Task<string> BalanceAsync(Guid itemId) => Database.ScalarAsync<string>($"SELECT CONCAT(OnHand, '|', Reserved) FROM InventoryItems WHERE Id = '{itemId}'");

    private Task<int> CountOtherOverdueAsync() =>
        Database.ScalarAsync<int>("SELECT COUNT(*) FROM Reservations WHERE Status = 'Active' AND ExpiresAt <= TODATETIMEOFFSET(SYSUTCDATETIME(), 0)");
}
