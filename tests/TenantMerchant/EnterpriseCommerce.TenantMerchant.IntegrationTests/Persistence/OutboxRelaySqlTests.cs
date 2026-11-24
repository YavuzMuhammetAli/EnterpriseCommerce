using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Persistence;

/// <summary>
/// The relay's database behaviour on a real SQL Server (PHASE_3_S4_EVENT_FLOW.md section 8): it reads committed rows
/// only, marks and records failures with short statements, cleans up only published rows, and one relay of several
/// publishes. A broken leader session moves the leadership. Kafka is replaced by a recording publisher here; the
/// broker behaviour is proven in the Kafka tests.
/// </summary>
public sealed class OutboxRelaySqlTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>, IAsyncLifetime
{
    public ValueTask InitializeAsync() =>
        database.IsConfigured ? new ValueTask(database.ExecuteAsync("DELETE FROM OutboxMessages")) : ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task TheRelay_NeverReadsAnUncommittedRow_AndTakesItOnceItCommits()
    {
        database.RequireAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new RelayHarness(database.ConnectionString);

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        long uncommitted;
        long committed;
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct))
        {
            // A business transaction that wrote its outbox row and has not committed yet.
            uncommitted = await OutboxRows.InsertAsync(database.ConnectionString, Guid.CreateVersion7(), 1, transaction: transaction, connection: connection);
            committed = await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);
            Assert.True(committed > uncommitted);

            Assert.Equal([committed], (await relay.Store.ReadPendingAsync(100, ct)).Select(row => row.Sequence));
            await relay.CycleAsync();
            Assert.Equal([committed], relay.Deliveries.Select(row => row.Sequence));

            await transaction.CommitAsync(ct);
        }

        await relay.CycleAsync();

        Assert.Equal([committed, uncommitted], relay.Deliveries.Select(row => row.Sequence));
        Assert.All(await OutboxRows.ReadAsync(database), row => Assert.NotNull(row.PublishedAt));
    }

    [Fact]
    public async Task ARolledBackRow_IsNeverPublished()
    {
        database.RequireAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new RelayHarness(database.ConnectionString);

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            await OutboxRows.InsertAsync(database.ConnectionString, Guid.CreateVersion7(), 1, transaction: transaction, connection: connection);
            await transaction.RollbackAsync(ct);
        }

        await relay.CycleAsync();

        Assert.Empty(relay.Deliveries);
        Assert.Empty(await OutboxRows.ReadAsync(database));
    }

    [Fact]
    public async Task MarkingAndFailureRecords_AreShortStatementsOnPendingRowsOnly()
    {
        database.RequireAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new RelayHarness(database.ConnectionString);
        var first = await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);
        var second = await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);
        var publishedAt = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

        await relay.Store.RecordFailureAsync(second, publishedAt, "Transient:Local_MsgTimedOut", ct);
        await relay.Store.RecordFailureAsync(second, publishedAt.AddSeconds(1), "Topic:UnknownTopicOrPart", ct);
        await relay.Store.MarkPublishedAsync([first], publishedAt, ct);
        await relay.Store.MarkPublishedAsync([first], publishedAt.AddHours(1), ct);

        var rows = await OutboxRows.ReadAsync(database);
        Assert.Equal(publishedAt, rows[0].PublishedAt);
        Assert.Equal((0, (string?)null), (rows[0].Attempts, rows[0].LastError));
        Assert.Equal((2, publishedAt.AddSeconds(1), "Topic:UnknownTopicOrPart"), (rows[1].Attempts, rows[1].LastAttemptAt!.Value, rows[1].LastError));
        Assert.Null(rows[1].PublishedAt);

        var backlog = await relay.Store.GetBacklogAsync(ct);
        Assert.Equal((1L, rows[1].OccurredAt), (backlog.Count, backlog.OldestOccurredAt!.Value));
    }

    [Fact]
    public async Task TheCleanup_RemovesOnlyPublishedRowsPastRetention_NeverAPendingOne()
    {
        database.RequireAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using var relay = new RelayHarness(database.ConnectionString);
        var old = await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);
        var recent = await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);
        var oldPending = await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);
        await database.ExecuteAsync(
            $"UPDATE OutboxMessages SET PublishedAt = DATEADD(day, -8, SYSDATETIMEOFFSET()) WHERE Sequence = {old};"
            + $" UPDATE OutboxMessages SET PublishedAt = DATEADD(day, -1, SYSDATETIMEOFFSET()) WHERE Sequence = {recent};"
            + $" UPDATE OutboxMessages SET OccurredAt = DATEADD(day, -30, SYSDATETIMEOFFSET()) WHERE Sequence = {oldPending};");

        var deleted = await relay.Store.DeletePublishedBeforeAsync(DateTimeOffset.UtcNow - relay.Settings.PublishedRetention, 1000, ct);

        Assert.Equal(1, deleted);
        Assert.Equal([recent, oldPending], (await OutboxRows.ReadAsync(database)).Select(row => row.Sequence));
    }

    [Fact]
    public async Task OfTwoRelaysOnOneDatabase_OnlyTheLeaderPublishes_AndTheStandbyTakesOverAfterItReleases()
    {
        database.RequireAvailable();
        await using var first = new RelayHarness(database.ConnectionString);
        await using var second = new RelayHarness(database.ConnectionString);
        await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);

        await first.CycleAsync();
        await second.CycleAsync();

        Assert.Single(first.Deliveries);
        Assert.Empty(second.Deliveries);
        Assert.Equal((true, false), (first.State.Current.IsLeader, second.State.Current.IsLeader));

        await first.Leadership.ReleaseAsync();
        var next = await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);
        await second.CycleAsync();

        Assert.Equal([next], second.Deliveries.Select(row => row.Sequence));
        Assert.True(second.State.Current.IsLeader);
    }

    [Fact]
    public async Task KillingTheLeadersSession_MovesTheLeadership()
    {
        database.RequireAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using var first = new SqlOutboxLeadership(() => database.ConnectionString, TenantMerchantOutbox.Profile.LockApplicationName);
        await using var second = new SqlOutboxLeadership(() => database.ConnectionString, TenantMerchantOutbox.Profile.LockApplicationName);

        Assert.True(await first.TryEnsureAsync(ct));
        Assert.False(await second.TryEnsureAsync(ct));
        Assert.True(await first.TryEnsureAsync(ct));

        // The leader's session dies (a crashed process, a network partition): SQL Server releases its lock.
        var session = await database.ScalarAsync<short>(
            "SELECT TOP (1) CAST(request_session_id AS smallint) FROM sys.dm_tran_locks"
            + " WHERE resource_type = 'APPLICATION' AND resource_database_id = DB_ID() AND request_owner_type = 'SESSION'");
        await database.ExecuteAsync($"KILL {session}");

        Assert.True(await second.TryEnsureAsync(ct));
        Assert.False(await first.TryEnsureAsync(ct));
    }

    [Fact]
    public async Task ACrashAfterDeliveryBeforeMarking_PublishesTheSameRowAgain_WithTheSameEventIdAndBytes()
    {
        database.RequireAvailable();
        var eventId = Guid.CreateVersion7();
        var sequence = await OutboxRows.InsertAsync(database, eventId, 1);

        await using (var crashing = new RelayHarness(database.ConnectionString, wrapStore: inner => new CrashBeforeMarkingStore(inner)))
        {
            await crashing.CycleAsync();
            Assert.Equal([eventId], crashing.Deliveries.Select(row => row.EventId));
        }

        Assert.Null(Assert.Single(await OutboxRows.ReadAsync(database)).PublishedAt);

        await using var restarted = new RelayHarness(database.ConnectionString);
        await restarted.CycleAsync();

        var again = Assert.Single(restarted.Deliveries);
        Assert.Equal((sequence, eventId), (again.Sequence, again.EventId));
        Assert.Equal((await OutboxRows.ReadAsync(database))[0].Envelope, again.Envelope);
        Assert.NotNull(Assert.Single(await OutboxRows.ReadAsync(database)).PublishedAt);
    }

    [Fact]
    public async Task AHostedRelay_PublishesInTheBackground_AndReleasesTheLeadershipOnShutdown()
    {
        database.RequireAvailable();
        var ct = TestContext.Current.CancellationToken;
        await using var hosted = new RelayHarness(database.ConnectionString);
        await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);
        await OutboxRows.InsertAsync(database, Guid.CreateVersion7(), 1);

        await hosted.Relay.StartAsync(ct);
        await Eventually.TrueAsync(async () => (await OutboxRows.ReadAsync(database)).All(row => row.PublishedAt is not null), TimeSpan.FromSeconds(30));
        await hosted.Relay.StopAsync(ct);

        await using var successor = new SqlOutboxLeadership(() => database.ConnectionString, TenantMerchantOutbox.Profile.LockApplicationName);
        Assert.True(await successor.TryEnsureAsync(ct));
        Assert.Equal(2, hosted.Deliveries.Count);
        Assert.False(hosted.State.Current.IsLeader);
    }
}
