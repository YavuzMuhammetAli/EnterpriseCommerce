using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EnterpriseCommerce.Identity.IntegrationTests.Security;

/// <summary>
/// The authoritative password reset and change transactions (ADR-017 section 8, ADR-018 section 1) against a real
/// SQL Server: atomicity, single use under concurrency, whole-transaction deadlock retry and fail-closed exhaustion.
/// </summary>
public sealed class PasswordTransactionTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private const string NewPassword = "Different-Staple-Lamp-7";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reset_CommitsPasswordVersionConsumptionAndSupersessionTogether()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var confirmation = await IssueAsync(services, userId, SecurityTokenPurpose.EmailConfirmation);
        await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);
        var reset = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);

        var result = await ResetAsync(services, reset.RawToken);

        Assert.Equal(PasswordUpdateStatus.Succeeded, result.Status);
        Assert.Equal(2, result.SecurityVersion);
        var state = await ReadAsync(services, userId);
        Assert.Equal(2, state.Version);
        Assert.NotNull(state.PasswordHash);
        Assert.True(await IsConsumedAsync(services, reset.TokenHash));
        Assert.True(await IsSupersededAsync(services, confirmation.TokenHash));

        // Single use: a replay changes nothing.
        Assert.Equal(PasswordUpdateStatus.Rejected, (await ResetAsync(services, reset.RawToken)).Status);
        Assert.Equal(2, (await ReadAsync(services, userId)).Version);
    }

    [Fact]
    public async Task Reset_WithAnExpiredToken_IsRejectedAndChangesNothing()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var expired = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset, DateTimeOffset.UtcNow.AddHours(-2));

        Assert.Equal(PasswordUpdateStatus.Rejected, (await ResetAsync(services, expired.RawToken)).Status);

        var state = await ReadAsync(services, userId);
        Assert.Null(state.PasswordHash);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public async Task Reset_WithAConfirmationToken_IsRejected()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var confirmation = await IssueAsync(services, userId, SecurityTokenPurpose.EmailConfirmation);

        Assert.Equal(PasswordUpdateStatus.Rejected, (await ResetAsync(services, confirmation.RawToken)).Status);
        Assert.False(await IsConsumedAsync(services, confirmation.TokenHash));
    }

    [Fact]
    public async Task Reset_ConcurrentConsumption_HasExactlyOneWinner()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var token = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => ResetAsync(services, token.RawToken), Ct)));

        Assert.Single(results, r => r.Status == PasswordUpdateStatus.Succeeded);
        Assert.Equal(3, results.Count(r => r.Status == PasswordUpdateStatus.Rejected));
        Assert.Equal(2, (await ReadAsync(services, userId)).Version);
    }

    [Fact]
    public async Task Reset_WhenThePasswordUpdateDoesNotApply_RollsBackTheConsumption()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var token = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);
        await UpdateUserAsync(services, userId, setters => setters.SetProperty(u => u.Status, Domain.Accounts.AccountStatus.Disabled));

        Assert.Equal(PasswordUpdateStatus.Rejected, (await ResetAsync(services, token.RawToken)).Status);

        Assert.False(await IsConsumedAsync(services, token.TokenHash));
        var state = await ReadAsync(services, userId);
        Assert.Null(state.PasswordHash);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public async Task Reset_WithoutASecurityVersionRecord_FailsClosedAndRollsEverythingBack()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var token = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);
        await ExecuteSqlAsync(services, $"DELETE FROM [SecurityVersions] WHERE [UserId] = {userId}");

        await Assert.ThrowsAsync<SecurityStateUnavailableException>(() => ResetAsync(services, token.RawToken));

        Assert.False(await IsConsumedAsync(services, token.TokenHash));
        Assert.Null((await ReadAsync(services, userId)).PasswordHash);
    }

    [Fact]
    public async Task Reset_PolicyFailure_NeverOpensTheTransaction()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var token = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);

        var result = await ResetAsync(services, token.RawToken, "short");

        Assert.Equal(PasswordUpdateStatus.PasswordRejected, result.Status);
        Assert.NotEmpty(result.PolicyErrors);
        Assert.False(await IsConsumedAsync(services, token.TokenHash));
    }

    [Fact]
    public async Task Reset_Deadlock_RetriesTheWholeTransaction_AndCommitsExactlyOnce()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        using var metrics = new MetricCollector(IdentityMetrics.MeterName);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var token = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);

        var (result, error) = await RunResetIntoDeadlockAsync(services, userId, token.RawToken);

        Assert.Null(error);
        Assert.Equal(PasswordUpdateStatus.Succeeded, result!.Status);
        Assert.True(metrics.Sum("identity.security.transaction.deadlock_retries", ResetPasswordHandler.Operation) >= 1);

        // The aborted attempt left nothing behind: one consumption, one increment, one new password.
        var state = await ReadAsync(services, userId);
        Assert.Equal(2, state.Version);
        Assert.NotNull(state.PasswordHash);
        Assert.True(await IsConsumedAsync(services, token.TokenHash));
    }

    [Fact]
    public async Task Reset_DeadlockRetriesExhausted_FailsClosed_AndTheTokenStaysUsable()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(
            database,
            new Dictionary<string, string?> { ["Identity:SecurityTransactions:MaxAttempts"] = "1" });
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var token = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);

        var (_, error) = await RunResetIntoDeadlockAsync(services, userId, token.RawToken);

        Assert.IsType<SecurityStateUnavailableException>(error);
        Assert.False(await IsConsumedAsync(services, token.TokenHash));
        var state = await ReadAsync(services, userId);
        Assert.Null(state.PasswordHash);
        Assert.Equal(1, state.Version);

        Assert.Equal(PasswordUpdateStatus.Succeeded, (await ResetAsync(services, token.RawToken)).Status);
    }

    [Fact]
    public async Task Change_ConcurrentChanges_HaveExactlyOneWinner()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        string currentHash;
        await using (var scope = services.CreateAsyncScope())
        {
            currentHash = scope.ServiceProvider.GetRequiredService<IAccountPasswordHasher>().Hash("Correct-Horse-Battery-9");
        }

        await UpdateUserAsync(services, userId, setters => setters.SetProperty(u => u.PasswordHash, currentHash));

        var results = await Task.WhenAll(
            Task.Run(() => ChangeAsync(services, userId, currentHash, "First-New-Password-1"), Ct),
            Task.Run(() => ChangeAsync(services, userId, currentHash, "Second-New-Password-2"), Ct));

        Assert.Single(results, r => r.Status == PasswordUpdateStatus.Succeeded);
        Assert.Single(results, r => r.Status == PasswordUpdateStatus.Rejected);
        Assert.Equal(2, (await ReadAsync(services, userId)).Version);
    }

    [Fact]
    public async Task Change_SupersedesOutstandingResetTokens_ButNotConfirmationTokens()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var reset = await IssueAsync(services, userId, SecurityTokenPurpose.PasswordReset);
        var confirmation = await IssueAsync(services, userId, SecurityTokenPurpose.EmailConfirmation);
        const string currentHash = "unused-current-hash";
        await UpdateUserAsync(services, userId, setters => setters.SetProperty(u => u.PasswordHash, currentHash));

        Assert.Equal(PasswordUpdateStatus.Succeeded, (await ChangeAsync(services, userId, currentHash, NewPassword)).Status);

        Assert.True(await IsSupersededAsync(services, reset.TokenHash));
        Assert.False(await IsSupersededAsync(services, confirmation.TokenHash));
    }

    [Fact]
    public async Task ResetEmail_MintsAThirtyMinuteTokenAndAResetLink_OnlyForAConfirmedAccount()
    {
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = IdentityTestServices.Build(database, configure: s =>
        {
            s.RemoveAll<ISecurityEmailDispatcher>();
            s.AddSingleton<ISecurityEmailDispatcher>(dispatcher);
        });
        var confirmedId = await IdentityTestServices.CreateUserAsync(services);
        var unconfirmedId = await IdentityTestServices.CreateUserAsync(services, emailConfirmed: false);

        // A reset delivery for an account that is no longer confirmed when the worker claims it is not sent.
        var confirmedDelivery = await CreateDeliveryAsync(services, confirmedId, SecurityTokenPurpose.PasswordReset);
        var staleDelivery = await CreateDeliveryAsync(services, unconfirmedId, SecurityTokenPurpose.PasswordReset);

        await using (var scope = services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<SecurityEmailDeliveryProcessor>();
            await processor.ProcessAsync(confirmedDelivery, Ct);
            await processor.ProcessAsync(staleDelivery, Ct);
        }

        var message = Assert.Single(dispatcher.Handovers).Message;
        Assert.Equal(SecurityTokenPurpose.PasswordReset, message.Purpose);
        Assert.Equal("https://localhost/account/reset-password", message.Link.GetLeftPart(UriPartial.Path));

        await using var check = services.CreateAsyncScope();
        var context = check.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        var token = await context.AccountSecurityTokens.AsNoTracking().SingleAsync(t => t.UserId == confirmedId, Ct);
        Assert.Equal(TimeSpan.FromMinutes(30), token.ExpiresAt - token.CreatedAt);
        Assert.Equal(SecurityTokenCodec.Hash(CapturingSecurityEmailDispatcher.TokenOf(message)), token.TokenHash);
        Assert.False(await context.AccountSecurityTokens.AnyAsync(t => t.UserId == unconfirmedId, Ct));
        Assert.Equal(
            SecurityEmailDeliveryStatus.Failed,
            await context.SecurityEmailDeliveries.Where(d => d.Id == staleDelivery).Select(d => d.Status).SingleAsync(Ct));
    }

    /// <summary>
    /// Forces a real SQL Server deadlock (1205) on the reset. A blocker transaction with high deadlock priority locks
    /// the security-version row; the reset consumes the token and updates the account, then waits for that row. Once
    /// the reset is observed waiting, the blocker updates the account row the reset holds: a cycle, and the reset (lower
    /// priority) is chosen as the victim.
    /// </summary>
    private static async Task<(PasswordUpdateResult? Result, Exception? Error)> RunResetIntoDeadlockAsync(IServiceProvider services, string userId, string token)
    {
        await using var blocker = new SqlConnection(ConnectionStringOf(services));
        await blocker.OpenAsync(Ct);
        await using var transaction = (SqlTransaction)await blocker.BeginTransactionAsync(Ct);
        // A plain batch: a SET inside a parameterized batch (sp_executesql) would revert when that batch ends.
        await using (var priority = new SqlCommand("SET DEADLOCK_PRIORITY HIGH", blocker, transaction))
        {
            await priority.ExecuteNonQueryAsync(Ct);
        }

        await RunAsync(blocker, transaction, "UPDATE [SecurityVersions] SET [Version] = [Version] WHERE [UserId] = @user", userId);

        var reset = Task.Run(
            async () =>
            {
                try
                {
                    return ((PasswordUpdateResult?)await ResetAsync(services, token), (Exception?)null);
                }
                catch (SecurityStateUnavailableException exception)
                {
                    return (null, exception);
                }
            },
            Ct);

        await SqlBlocking.WaitUntilBlockedAsync(ConnectionStringOf(services));
        await RunAsync(blocker, transaction, "UPDATE [AspNetUsers] SET [ConcurrencyStamp] = [ConcurrencyStamp] WHERE [Id] = @user", userId);
        await transaction.CommitAsync(Ct);

        return await reset;
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

    private static async Task<PasswordUpdateResult> ResetAsync(IServiceProvider services, string token, string password = NewPassword)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ResetPasswordHandler>().HandleAsync(token, password, Ct);
    }

    private static async Task<PasswordUpdateResult> ChangeAsync(IServiceProvider services, string userId, string currentHash, string password)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ChangePasswordHandler>().HandleAsync(userId, currentHash, password, Ct);
    }

    private static async Task<GeneratedSecurityToken> IssueAsync(IServiceProvider services, string userId, SecurityTokenPurpose purpose, DateTimeOffset? createdAt = null)
    {
        var generated = SecurityTokenCodec.Generate();
        await services.GetRequiredService<IAuthoritativeSecurityTransaction>().ExecuteAsync(
            "test-issue",
            async (unit, ct) =>
            {
                await unit.AddSecurityTokenAsync(
                    AccountSecurityToken.Issue(userId, purpose, generated.TokenHash, createdAt ?? DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30)),
                    ct);
                return TransactionDecision.Commit(true);
            },
            Ct);
        return generated;
    }

    private static Task<Guid> CreateDeliveryAsync(IServiceProvider services, string userId, SecurityTokenPurpose purpose) =>
        services.GetRequiredService<IAuthoritativeSecurityTransaction>().ExecuteAsync(
            "test-delivery",
            async (unit, ct) => TransactionDecision.Commit(await unit.CreateSecurityEmailDeliveryAsync(userId, purpose, ct)),
            Ct);

    private static async Task ExecuteSqlAsync(IServiceProvider services, FormattableString sql)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Database.ExecuteSqlAsync(sql, Ct);
    }

    private static async Task UpdateUserAsync(
        IServiceProvider services,
        string userId,
        Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<ApplicationUser>> setters)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(setters, Ct);
    }

    private static async Task<(string? PasswordHash, long? Version)> ReadAsync(IServiceProvider services, string userId)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        var hash = await context.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PasswordHash).SingleAsync(Ct);
        return (hash, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));
    }

    private static async Task<bool> IsConsumedAsync(IServiceProvider services, string tokenHash)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().AccountSecurityTokens
            .AsNoTracking().AnyAsync(t => t.TokenHash == tokenHash && t.ConsumedAt != null, Ct);
    }

    private static async Task<bool> IsSupersededAsync(IServiceProvider services, string tokenHash)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().AccountSecurityTokens
            .AsNoTracking().AnyAsync(t => t.TokenHash == tokenHash && t.SupersededAt != null, Ct);
    }
}
