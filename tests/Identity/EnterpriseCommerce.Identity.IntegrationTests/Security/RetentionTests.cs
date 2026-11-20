using System.Data;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.Infrastructure.Retention;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Identity.IntegrationTests.Security;

/// <summary>
/// Retention cleanup (Phase 2.3 S6) against a real SQL Server. The test services use a 1-hour token lifetime and the
/// default 1-day safety margin, so the replay protection window is 25 hours: rows aged 3 days are eligible, rows aged
/// 2 hours are not.
/// </summary>
public sealed class RetentionTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(25);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheWindow_IsTheLongestLifetimePlusTheSafetyMargin()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);

        Assert.Equal(Window, services.GetRequiredService<IOptions<IdentityRetentionOptions>>().Value.ReplayProtectionWindow);
    }

    [Fact]
    public async Task Ledger_DeletesOnlyRowsOlderThanTheWindow_InBoundedBatchesPerRun()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database, new Dictionary<string, string?>
        {
            ["Identity:Retention:BatchSize"] = "1000",
            ["Identity:Retention:MaxBatchesPerRun"] = "2",
        });
        var marker = Guid.NewGuid().ToString("N");
        await InsertLedgerRowsAsync(marker + "-old", 2500, TimeSpan.FromDays(3));
        await InsertLedgerRowsAsync(marker + "-new", 10, TimeSpan.FromHours(2));
        using var metrics = new MetricCollector(IdentityMetrics.MeterName);

        var first = await RunAsync(services);

        Assert.Equal(RetentionRunOutcome.Completed, first.Outcome);
        Assert.Equal(2000, first.LedgerRows);
        Assert.Equal(500, await CountLedgerAsync(marker + "-old"));

        var second = await RunAsync(services);

        Assert.Equal(500, second.LedgerRows);
        Assert.Equal(0, await CountLedgerAsync(marker + "-old"));
        Assert.Equal(10, await CountLedgerAsync(marker + "-new"));
        Assert.Equal(0, (await RunAsync(services)).LedgerRows);
    }

    [Fact]
    public async Task Ledger_NeverDeletesWhatAnInFlightTransactionHolds_AndWaitsOnlyBriefly()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database, new Dictionary<string, string?>
        {
            ["Identity:Retention:LockTimeout"] = "00:00:00.300",
            ["Identity:Retention:MaxAttempts"] = "2",
        });
        var marker = Guid.NewGuid().ToString("N");
        await InsertLedgerRowsAsync(marker, 3, TimeSpan.FromDays(3));
        using var metrics = new MetricCollector(IdentityMetrics.MeterName);

        await using (var connection = new SqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
            await using (var command = new SqlCommand("UPDATE [RefreshTokenRedemptions] SET [AuthorizationId] = [AuthorizationId] WHERE [TokenId] = @id", connection, transaction))
            {
                command.Parameters.AddWithValue("@id", marker + "-0");
                Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
            }

            // The run gives up on the locked batch after the bounded lock waits instead of blocking, and deletes nothing
            // the open transaction holds. Other categories still ran.
            var run = await RunAsync(services).WaitAsync(TimeSpan.FromSeconds(20), Ct);

            Assert.Equal(RetentionRunOutcome.Failed, run.Outcome);
            Assert.Equal(3, await CountLedgerAsync(marker));
            await transaction.CommitAsync(Ct);
        }

        // Once the transaction has ended, the next run completes the cleanup.
        var next = await RunAsync(services);
        Assert.Equal(RetentionRunOutcome.Completed, next.Outcome);
        Assert.Equal(0, await CountLedgerAsync(marker));
    }

    [Fact]
    public async Task OpenIddict_PrunesOnlyEntriesThatCanNoLongerBeAccepted()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var subject = Guid.NewGuid().ToString("N");

        var oldRevokedAuthorization = await InsertAuthorizationAsync(subject, "revoked", TimeSpan.FromDays(3));
        var oldRedeemed = await InsertTokenAsync(subject, null, "redeemed", created: TimeSpan.FromDays(3), expiresIn: TimeSpan.FromDays(-2));
        var recentRedeemed = await InsertTokenAsync(subject, null, "redeemed", created: TimeSpan.FromHours(2), expiresIn: TimeSpan.FromHours(-1));
        var liveAuthorization = await InsertAuthorizationAsync(subject, "valid", TimeSpan.FromDays(3));
        var liveToken = await InsertTokenAsync(subject, liveAuthorization, "valid", created: TimeSpan.FromDays(3), expiresIn: TimeSpan.FromDays(1));

        var result = await RunAsync(services);

        Assert.Equal(RetentionRunOutcome.Completed, result.Outcome);
        Assert.False(await ExistsAsync("OpenIddictTokens", oldRedeemed));
        Assert.False(await ExistsAsync("OpenIddictAuthorizations", oldRevokedAuthorization));

        // Inside the window, or still acceptable: kept.
        Assert.True(await ExistsAsync("OpenIddictTokens", recentRedeemed));
        Assert.True(await ExistsAsync("OpenIddictTokens", liveToken));
        Assert.True(await ExistsAsync("OpenIddictAuthorizations", liveAuthorization));
    }

    [Fact]
    public async Task WhenAnotherInstanceHoldsTheLock_TheRunIsSkippedAndDeletesNothing()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var marker = Guid.NewGuid().ToString("N");
        await InsertLedgerRowsAsync(marker, 5, TimeSpan.FromDays(3));

        await using (var otherInstance = new SqlConnection(database.ConnectionString))
        {
            await otherInstance.OpenAsync(Ct);
            Assert.True(await GetAppLockAsync(otherInstance) >= 0);

            var skipped = await RunAsync(services);

            Assert.Equal(RetentionRunOutcome.Skipped, skipped.Outcome);
            Assert.Equal(5, await CountLedgerAsync(marker));
            await ReleaseAppLockAsync(otherInstance);
        }

        Assert.Equal(RetentionRunOutcome.Completed, (await RunAsync(services)).Outcome);
        Assert.Equal(0, await CountLedgerAsync(marker));
    }

    [Fact]
    public async Task TwoInstancesRunningAtOnce_NeverFailAndDeleteEveryEligibleRowOnce()
    {
        database.RequireAvailable();
        await using var first = IdentityTestServices.Build(database);
        await using var second = IdentityTestServices.Build(database);
        var marker = Guid.NewGuid().ToString("N");
        await InsertLedgerRowsAsync(marker, 3000, TimeSpan.FromDays(3));

        var results = await Task.WhenAll(Task.Run(() => RunAsync(first), Ct), Task.Run(() => RunAsync(second), Ct));

        Assert.DoesNotContain(results, r => r.Outcome == RetentionRunOutcome.Failed);
        Assert.Contains(results, r => r.Outcome == RetentionRunOutcome.Completed);
        Assert.Equal(0, await CountLedgerAsync(marker));
    }

    [Fact]
    public async Task WithAnUnreachableDatabase_TheRunFailsQuicklyWithoutThrowing()
    {
        database.RequireAvailable();
        var unreachable = new SqlConnectionStringBuilder(database.ConnectionString) { DataSource = "tcp:127.0.0.1,1", ConnectTimeout = 2 }.ConnectionString;
        await using var services = IdentityTestServices.Build(database, new Dictionary<string, string?> { ["ConnectionStrings:IdentityDatabase"] = unreachable });
        using var metrics = new MetricCollector(IdentityMetrics.MeterName);

        var result = await RunAsync(services).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(RetentionRunOutcome.Failed, result.Outcome);
        var state = services.GetRequiredService<BackgroundWorkerHeartbeats>().Retention;
        Assert.NotNull(state.LastRunAt);
        Assert.Null(state.LastSuccessAt);
    }

    [Fact]
    public async Task ACancelledRun_StopsPromptly()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.GetRequiredService<IdentityRetentionRunner>().RunOnceAsync(cancelled.Token));
    }

    [Fact]
    public async Task TheWorker_StopsPromptlyDuringItsInitialDelay()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var worker = services.GetServices<IHostedService>().OfType<IdentityRetentionWorker>().Single();

        await worker.StartAsync(Ct);
        await worker.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Null(services.GetRequiredService<BackgroundWorkerHeartbeats>().Retention.LastRunAt);
    }

    private static async Task<RetentionRunResult> RunAsync(IServiceProvider services) =>
        await services.GetRequiredService<IdentityRetentionRunner>().RunOnceAsync(Ct);

    private async Task InsertLedgerRowsAsync(string prefix, int count, TimeSpan age)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(
            """
            INSERT INTO [RefreshTokenRedemptions] ([TokenId], [AuthorizationId], [RedeemedAt])
            SELECT TOP (@count) CONCAT(@prefix, '-', ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1), NULL, DATEADD(SECOND, -@age, SYSDATETIMEOFFSET())
            FROM sys.all_objects a CROSS JOIN sys.all_objects b
            """,
            connection);
        command.Parameters.AddWithValue("@count", count);
        command.Parameters.AddWithValue("@prefix", prefix);
        command.Parameters.AddWithValue("@age", (int)age.TotalSeconds);
        Assert.Equal(count, await command.ExecuteNonQueryAsync(Ct));
    }

    private async Task<int> CountLedgerAsync(string prefix)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM [RefreshTokenRedemptions] WHERE [TokenId] LIKE @prefix + '%'", connection);
        command.Parameters.AddWithValue("@prefix", prefix);
        return (int)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string> InsertAuthorizationAsync(string subject, string status, TimeSpan age)
    {
        var id = Guid.NewGuid().ToString();
        await ExecuteAsync(
            "INSERT INTO [OpenIddictAuthorizations] ([Id], [ConcurrencyToken], [CreationDate], [Status], [Type], [Subject]) VALUES (@id, 'c', DATEADD(SECOND, -@age, SYSUTCDATETIME()), @status, 'ad-hoc', @subject)",
            ("@id", id),
            ("@age", (int)age.TotalSeconds),
            ("@status", status),
            ("@subject", subject));
        return id;
    }

    /// <param name="created">How long ago the token was created.</param>
    /// <param name="expiresIn">When it expires, relative to now; negative for an expiration in the past.</param>
    private async Task<string> InsertTokenAsync(string subject, string? authorizationId, string status, TimeSpan created, TimeSpan expiresIn)
    {
        var id = Guid.NewGuid().ToString();
        await ExecuteAsync(
            """
            INSERT INTO [OpenIddictTokens] ([Id], [AuthorizationId], [ConcurrencyToken], [CreationDate], [ExpirationDate], [Status], [Type], [Subject])
            VALUES (@id, @authorization, 'c', DATEADD(SECOND, -@created, SYSUTCDATETIME()), DATEADD(SECOND, @expiresIn, SYSUTCDATETIME()), @status, 'refresh_token', @subject)
            """,
            ("@id", id),
            ("@authorization", (object?)authorizationId ?? DBNull.Value),
            ("@created", (int)created.TotalSeconds),
            ("@expiresIn", (int)expiresIn.TotalSeconds),
            ("@status", status),
            ("@subject", subject));
        return id;
    }

    private async Task<bool> ExistsAsync(string table, string id)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand($"SELECT COUNT(*) FROM [{table}] WHERE [Id] = @id", connection);
        command.Parameters.AddWithValue("@id", id);
        return (int)(await command.ExecuteScalarAsync(Ct))! == 1;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        Assert.Equal(1, await command.ExecuteNonQueryAsync(Ct));
    }

    private static async Task<int> GetAppLockAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("sp_getapplock", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Resource", IdentityRetentionRunner.LockResource);
        command.Parameters.AddWithValue("@LockMode", "Exclusive");
        command.Parameters.AddWithValue("@LockOwner", "Session");
        command.Parameters.AddWithValue("@LockTimeout", 0);
        var result = command.Parameters.Add("@ReturnValue", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;
        await command.ExecuteNonQueryAsync(Ct);
        return (int)result.Value;
    }

    private static async Task ReleaseAppLockAsync(SqlConnection connection)
    {
        await using var command = new SqlCommand("sp_releaseapplock", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@Resource", IdentityRetentionRunner.LockResource);
        command.Parameters.AddWithValue("@LockOwner", "Session");
        await command.ExecuteNonQueryAsync(Ct);
    }
}
