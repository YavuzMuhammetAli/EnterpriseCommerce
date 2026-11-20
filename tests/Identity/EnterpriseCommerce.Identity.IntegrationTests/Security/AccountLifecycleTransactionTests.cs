using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Security;

/// <summary>
/// The authoritative account disablement and reactivation transactions (ADR-017 sections 7 and 8,
/// PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.6.6) against a real SQL Server: atomicity, exactly one change under
/// concurrency, whole-transaction deadlock retry and fail-closed exhaustion.
/// </summary>
public sealed class AccountLifecycleTransactionTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disable_CommitsStatusVersionStampAndSupersessionTogether()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var before = await ReadUserAsync(services, userId);
        var reset = await AddTokenAsync(services, userId, SecurityTokenPurpose.PasswordReset);
        var confirmation = await AddTokenAsync(services, userId, SecurityTokenPurpose.EmailConfirmation);

        var result = await DisableAsync(services, userId, AccountStatusReason.SecurityConcern);

        Assert.Equal(AccountLifecycleOutcome.Changed, result.Outcome);
        Assert.Equal(AccountStatus.Disabled, result.Status);
        Assert.Equal(2, result.SecurityVersion);

        var after = await ReadUserAsync(services, userId);
        Assert.Equal(AccountStatus.Disabled, after.Status);
        Assert.Equal(AccountStatusReason.SecurityConcern, after.StatusReason);
        Assert.Equal("admin:alice", after.StatusChangedBy);
        Assert.NotNull(after.StatusChangedAt);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.NotEqual(before.ConcurrencyStamp, after.ConcurrencyStamp);
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));

        // Every outstanding security token is superseded, so no emailed link can act on the disabled account.
        Assert.True(await IsSupersededAsync(services, reset));
        Assert.True(await IsSupersededAsync(services, confirmation));
    }

    [Fact]
    public async Task Disable_ConcurrentRequests_ChangeTheAccountExactlyOnce()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);

        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                async () =>
                {
                    start.Wait(Ct);
                    return await DisableAsync(services, userId);
                },
                Ct))
            .ToList();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r.Outcome == AccountLifecycleOutcome.Changed);
        Assert.All(results, r => Assert.Equal(AccountStatus.Disabled, r.Status));
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));
    }

    [Fact]
    public async Task Disable_AnAlreadyDisabledAccount_ChangesNothingAndKeepsTheVersion()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        await DisableAsync(services, userId);
        var stamp = (await ReadUserAsync(services, userId)).SecurityStamp;

        var repeated = await DisableAsync(services, userId, AccountStatusReason.PolicyViolation);

        Assert.Equal(AccountLifecycleOutcome.Unchanged, repeated.Outcome);
        Assert.Null(repeated.SecurityVersion);
        var user = await ReadUserAsync(services, userId);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.Equal(AccountStatusReason.Administrative, user.StatusReason);
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));
    }

    [Fact]
    public async Task Disable_AnUnknownAccount_IsNotFound()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);

        var result = await DisableAsync(services, Guid.NewGuid().ToString());

        Assert.Equal(AccountLifecycleOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Disable_WithoutASecurityVersionRecord_FailsClosedAndChangesNothing()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>()
                .SecurityVersions.Where(r => r.UserId == userId).ExecuteDeleteAsync(Ct);
        }

        await Assert.ThrowsAsync<SecurityStateUnavailableException>(() => DisableAsync(services, userId));

        Assert.Equal(AccountStatus.Active, (await ReadUserAsync(services, userId)).Status);
    }

    [Fact]
    public async Task Disable_Deadlocked_IsRetriedAsAWholeAndCommitsOnce()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        using var metrics = new MetricCollector(IdentitySecurityMetrics.MeterName);

        var (result, error) = await RunDisableIntoDeadlockAsync(services, userId);

        Assert.Null(error);
        Assert.Equal(AccountLifecycleOutcome.Changed, result!.Outcome);
        Assert.True(metrics.Sum("identity.security.transaction.deadlock_retries", DisableAccountHandler.Operation) >= 1);
        Assert.Equal(AccountStatus.Disabled, (await ReadUserAsync(services, userId)).Status);
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));
    }

    [Fact]
    public async Task Disable_DeadlockRetriesExhausted_FailsClosedAndLeavesTheAccountActive()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(
            database,
            new Dictionary<string, string?> { ["Identity:SecurityTransactions:MaxAttempts"] = "1" });
        var userId = await IdentityTestServices.CreateUserAsync(services);
        using var metrics = new MetricCollector(IdentitySecurityMetrics.MeterName);

        var (_, error) = await RunDisableIntoDeadlockAsync(services, userId);

        Assert.IsType<SecurityStateUnavailableException>(error);
        Assert.Equal(1, metrics.Sum("identity.security.transaction.failures", DisableAccountHandler.Operation));
        Assert.Equal(AccountStatus.Active, (await ReadUserAsync(services, userId)).Status);
        Assert.Equal(1, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));

        // Nothing is half-done: a later request disables the account normally.
        Assert.Equal(AccountLifecycleOutcome.Changed, (await DisableAsync(services, userId)).Outcome);
    }

    [Fact]
    public async Task Reactivate_RestoresTheStatusOnly_AndKeepsTheRaisedVersion()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        await DisableAsync(services, userId);
        var disabledStamp = (await ReadUserAsync(services, userId)).SecurityStamp;

        var result = await ReactivateAsync(services, userId);
        var repeated = await ReactivateAsync(services, userId);

        Assert.Equal(AccountLifecycleOutcome.Changed, result.Outcome);
        Assert.Equal(AccountLifecycleOutcome.Unchanged, repeated.Outcome);
        var user = await ReadUserAsync(services, userId);
        Assert.Equal(AccountStatus.Active, user.Status);
        Assert.Equal(AccountStatusReason.UserRequest, user.StatusReason);
        Assert.Equal(disabledStamp, user.SecurityStamp);
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));
        Assert.Equal(AccountLifecycleOutcome.NotFound, (await ReactivateAsync(services, Guid.NewGuid().ToString())).Outcome);
    }

    /// <summary>
    /// Forces a real SQL Server deadlock (1205) on the disablement. A blocker transaction with high deadlock priority
    /// locks the security-version row; the disablement updates the account row, then waits for that row. Once it is
    /// observed waiting, the blocker updates the account row: a cycle, and the disablement is chosen as the victim.
    /// </summary>
    private static async Task<(AccountLifecycleResult? Result, Exception? Error)> RunDisableIntoDeadlockAsync(IServiceProvider services, string userId)
    {
        var connectionString = ConnectionStringOf(services);
        await using var blocker = new SqlConnection(connectionString);
        await blocker.OpenAsync(Ct);
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(Ct);
        await using (var priority = new SqlCommand("SET DEADLOCK_PRIORITY HIGH", blocker, transaction))
        {
            await priority.ExecuteNonQueryAsync(Ct);
        }

        await RunAsync(blocker, transaction, "UPDATE [SecurityVersions] SET [Version] = [Version] WHERE [UserId] = @user", userId);

        var disable = Task.Run(
            async () =>
            {
                try
                {
                    return ((AccountLifecycleResult?)await DisableAsync(services, userId), (Exception?)null);
                }
                catch (SecurityStateUnavailableException exception)
                {
                    return (null, exception);
                }
            },
            Ct);

        await SqlBlocking.WaitUntilBlockedAsync(connectionString);
        await RunAsync(blocker, transaction, "UPDATE [AspNetUsers] SET [ConcurrencyStamp] = [ConcurrencyStamp] WHERE [Id] = @user", userId);
        await transaction.CommitAsync(Ct);

        return await disable;
    }

    private static async Task<AccountLifecycleResult> DisableAsync(
        IServiceProvider services,
        string userId,
        AccountStatusReason reason = AccountStatusReason.Administrative)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DisableAccountHandler>()
            .HandleAsync(new AccountStatusChange(userId, reason, "admin:alice", "svc-admin"), Ct);
    }

    private static async Task<AccountLifecycleResult> ReactivateAsync(IServiceProvider services, string userId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ReactivateAccountHandler>()
            .HandleAsync(new AccountStatusChange(userId, AccountStatusReason.UserRequest, "admin:bob", "svc-admin"), Ct);
    }

    private static async Task<ApplicationUser> ReadUserAsync(IServiceProvider services, string userId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>()
            .Users.AsNoTracking().SingleAsync(u => u.Id == userId, Ct);
    }

    private static async Task<string> AddTokenAsync(IServiceProvider services, string userId, SecurityTokenPurpose purpose)
    {
        var hash = SecurityTokenCodec.Generate().TokenHash;
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        context.AccountSecurityTokens.Add(AccountSecurityToken.Issue(userId, purpose, hash, DateTimeOffset.UtcNow, TimeSpan.FromHours(1)));
        await context.SaveChangesAsync(Ct);
        return hash;
    }

    private static async Task<bool> IsSupersededAsync(IServiceProvider services, string tokenHash)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>()
            .AccountSecurityTokens.AsNoTracking().AnyAsync(t => t.TokenHash == tokenHash && t.SupersededAt != null, Ct);
    }

    private static async Task RunAsync(SqlConnection connection, SqlTransaction transaction, string sql, string userId)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@user", userId);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static string ConnectionStringOf(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Database.GetConnectionString()!;
    }
}
