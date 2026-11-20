using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.IntegrationTests.Security;

/// <summary>
/// The authoritative transaction on a real SQL Server (ADR-017 section 8, ADR-018 section 1):
/// single-use consumption under concurrency, rollback, and whole-transaction deadlock retry.
/// </summary>
public sealed class AuthoritativeSecurityTransactionTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ConcurrentConsumption_OfOneToken_HasExactlyOneWinner()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var transaction = services.GetRequiredService<IAuthoritativeSecurityTransaction>();
        var userId = await IdentityTestServices.CreateUserAsync(services);
        const int rounds = 20;
        const int consumersPerRound = 8;

        for (var round = 0; round < rounds; round++)
        {
            var token = await IssueTokenAsync(transaction, userId, SecurityTokenPurpose.PasswordReset);
            using var start = new ManualResetEventSlim(false);

            var consumers = Enumerable.Range(0, consumersPerRound).Select(_ => Task.Run(
                async () =>
                {
                    start.Wait(TestContext.Current.CancellationToken);
                    return await transaction.ExecuteAsync(
                        "test-consume-race",
                        async (unit, ct) => TransactionDecision.Commit(
                            await unit.TryConsumeSecurityTokenAsync(token.TokenHash, SecurityTokenPurpose.PasswordReset, DateTimeOffset.UtcNow, ct)),
                        TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken)).ToArray();

            start.Set();
            var winners = (await Task.WhenAll(consumers)).Where(owner => owner is not null).ToList();

            Assert.Equal([userId], winners);
        }
    }

    [Fact]
    public async Task RolledBackConsumption_LeavesTheTokenUsable()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var transaction = services.GetRequiredService<IAuthoritativeSecurityTransaction>();
        var userId = await IdentityTestServices.CreateUserAsync(services);
        var token = await IssueTokenAsync(transaction, userId, SecurityTokenPurpose.PasswordReset);

        // A failing account change (for example a password that fails the policy) rolls everything back.
        var consumedThenRolledBack = await transaction.ExecuteAsync(
            "test-rollback",
            async (unit, ct) =>
            {
                var owner = await unit.TryConsumeSecurityTokenAsync(token.TokenHash, SecurityTokenPurpose.PasswordReset, DateTimeOffset.UtcNow, ct);
                await unit.IncrementSecurityVersionAsync(userId, ct);
                return TransactionDecision.Rollback(owner);
            },
            TestContext.Current.CancellationToken);

        // An exception inside the work rolls back too.
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ExecuteAsync<string?>(
            "test-rollback-exception",
            async (unit, ct) =>
            {
                await unit.TryConsumeSecurityTokenAsync(token.TokenHash, SecurityTokenPurpose.PasswordReset, DateTimeOffset.UtcNow, ct);
                throw new InvalidOperationException("Simulated failure after consumption.");
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(userId, consumedThenRolledBack);
        Assert.Equal(SecurityVersionPolicy.InitialVersion, await IdentityTestServices.ReadSecurityVersionAsync(services, userId));
        Assert.Equal(userId, await ConsumeAsync(transaction, token.TokenHash, SecurityTokenPurpose.PasswordReset));
        Assert.Null(await ConsumeAsync(transaction, token.TokenHash, SecurityTokenPurpose.PasswordReset));
    }

    [Fact]
    public async Task Consumption_RejectsWrongPurposeExpiredAndSupersededTokens()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        var transaction = services.GetRequiredService<IAuthoritativeSecurityTransaction>();
        var userId = await IdentityTestServices.CreateUserAsync(services);

        var first = await IssueTokenAsync(transaction, userId, SecurityTokenPurpose.EmailConfirmation);
        var second = await IssueTokenAsync(transaction, userId, SecurityTokenPurpose.EmailConfirmation);
        var expired = await IssueTokenAsync(transaction, userId, SecurityTokenPurpose.PasswordReset, TimeSpan.FromMilliseconds(1));
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Null(await ConsumeAsync(transaction, second.TokenHash, SecurityTokenPurpose.PasswordReset));
        Assert.Null(await ConsumeAsync(transaction, first.TokenHash, SecurityTokenPurpose.EmailConfirmation));
        Assert.Null(await ConsumeAsync(transaction, expired.TokenHash, SecurityTokenPurpose.PasswordReset));
        Assert.Equal(userId, await ConsumeAsync(transaction, second.TokenHash, SecurityTokenPurpose.EmailConfirmation));
    }

    [Fact]
    public async Task Deadlock_RetriesTheWholeTransaction_AndBothChangesCommit()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);
        using var metrics = new MetricCollector(IdentityMetrics.MeterName);
        var transaction = services.GetRequiredService<IAuthoritativeSecurityTransaction>();
        var first = await IdentityTestServices.CreateUserAsync(services);
        var second = await IdentityTestServices.CreateUserAsync(services);
        var operation = $"test-deadlock-{Guid.NewGuid():N}";

        var results = await RunOppositeOrderIncrementsAsync(transaction, operation, first, second);

        Assert.All(results, outcome => Assert.Null(outcome));
        Assert.Equal(3, await IdentityTestServices.ReadSecurityVersionAsync(services, first));
        Assert.Equal(3, await IdentityTestServices.ReadSecurityVersionAsync(services, second));
        Assert.True(metrics.Sum("identity.security.transaction.deadlock_retries", operation) >= 1);
        Assert.Equal(0, metrics.Sum("identity.security.transaction.failures", operation));
    }

    [Fact]
    public async Task Deadlock_WhenRetriesAreExhausted_FailsClosedAndCommitsNothingForTheVictim()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(
            database,
            new Dictionary<string, string?> { ["Identity:SecurityTransactions:MaxAttempts"] = "1" });
        using var metrics = new MetricCollector(IdentityMetrics.MeterName);
        var transaction = services.GetRequiredService<IAuthoritativeSecurityTransaction>();
        var first = await IdentityTestServices.CreateUserAsync(services);
        var second = await IdentityTestServices.CreateUserAsync(services);
        var operation = $"test-deadlock-exhausted-{Guid.NewGuid():N}";

        var results = await RunOppositeOrderIncrementsAsync(transaction, operation, first, second);

        // Exactly one transaction was chosen as the victim. Only the winner's increments are committed.
        Assert.Single(results, outcome => outcome is SecurityStateUnavailableException);
        Assert.Single(results, outcome => outcome is null);
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, first));
        Assert.Equal(2, await IdentityTestServices.ReadSecurityVersionAsync(services, second));
        Assert.Equal(1, metrics.Sum("identity.security.transaction.failures", operation));
    }

    [Fact]
    public async Task UnreachableDatabase_FailsClosedWithSecurityStateUnavailable()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(
            database,
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:IdentityDatabase"] = "Server=tcp:127.0.0.1,1;Database=unreachable;Connect Timeout=2;TrustServerCertificate=true;Integrated Security=true",
            });
        var transaction = services.GetRequiredService<IAuthoritativeSecurityTransaction>();

        await Assert.ThrowsAsync<SecurityStateUnavailableException>(() => transaction.ExecuteAsync(
            "test-unreachable",
            async (unit, ct) => TransactionDecision.Commit(await unit.IncrementSecurityVersionAsync("any-user", ct)),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Two transactions lock the same two security-version rows in opposite order, which forces a real SQL Server
    /// deadlock (error 1205). Each returns <see langword="null"/> on success or the exception it ended with.
    /// </summary>
    private static async Task<Exception?[]> RunOppositeOrderIncrementsAsync(
        IAuthoritativeSecurityTransaction transaction,
        string operation,
        string first,
        string second)
    {
        var firstLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<Exception?> Run(string lockFirst, string lockSecond, TaskCompletionSource mine, TaskCompletionSource theirs) => Task.Run(
            async () =>
            {
                try
                {
                    await transaction.ExecuteAsync(
                        operation,
                        async (unit, ct) =>
                        {
                            await unit.IncrementSecurityVersionAsync(lockFirst, ct);
                            mine.TrySetResult();

                            // On a retry both signals are already set, so the retried attempt does not wait.
                            await theirs.Task.WaitAsync(SignalTimeout, ct);
                            await unit.IncrementSecurityVersionAsync(lockSecond, ct);
                            return TransactionDecision.Commit(true);
                        },
                        TestContext.Current.CancellationToken);
                    return (Exception?)null;
                }
                catch (SecurityStateUnavailableException exception)
                {
                    return exception;
                }
            },
            TestContext.Current.CancellationToken);

        return await Task.WhenAll(
            Run(first, second, firstLocked, secondLocked),
            Run(second, first, secondLocked, firstLocked));
    }

    private static async Task<GeneratedSecurityToken> IssueTokenAsync(
        IAuthoritativeSecurityTransaction transaction,
        string userId,
        SecurityTokenPurpose purpose,
        TimeSpan? lifetime = null)
    {
        var generated = SecurityTokenCodec.Generate();
        await transaction.ExecuteAsync(
            "test-issue",
            async (unit, ct) =>
            {
                await unit.AddSecurityTokenAsync(
                    AccountSecurityToken.Issue(userId, purpose, generated.TokenHash, DateTimeOffset.UtcNow, lifetime ?? TimeSpan.FromMinutes(30)),
                    ct);
                return TransactionDecision.Commit(true);
            },
            TestContext.Current.CancellationToken);
        return generated;
    }

    private static Task<string?> ConsumeAsync(IAuthoritativeSecurityTransaction transaction, string tokenHash, SecurityTokenPurpose purpose) =>
        transaction.ExecuteAsync(
            "test-consume",
            async (unit, ct) => TransactionDecision.Commit(await unit.TryConsumeSecurityTokenAsync(tokenHash, purpose, DateTimeOffset.UtcNow, ct)),
            TestContext.Current.CancellationToken);
}
