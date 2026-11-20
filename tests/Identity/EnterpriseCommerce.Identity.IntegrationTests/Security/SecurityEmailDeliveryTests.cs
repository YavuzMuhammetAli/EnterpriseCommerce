using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EnterpriseCommerce.Identity.IntegrationTests.Security;

/// <summary>
/// The delivery state machine on a real SQL Server (ADR-018 section 3): bounded queue, worker claim, minting after the
/// claim, bounded retries with one token and one identity, and status-only reconciliation. No host runs here, so the
/// tests drive the worker steps and the reconciler directly.
/// </summary>
public sealed class SecurityEmailDeliveryTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task FullQueue_RecordsTheDeliveryAsDropped()
    {
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = Build(dispatcher, new Dictionary<string, string?> { ["Identity:SecurityEmail:QueueCapacity"] = "1" });
        using var metrics = new MetricCollector(IdentityMetrics.MeterName);
        var userId = await IdentityTestServices.CreateUserAsync(services, emailConfirmed: false);
        var first = await CreateDeliveryAsync(services, userId);
        var second = await CreateDeliveryAsync(services, userId);

        await using (var scope = services.CreateAsyncScope())
        {
            var scheduler = scope.ServiceProvider.GetRequiredService<SecurityEmailScheduler>();
            await scheduler.ScheduleAsync(first, TestContext.Current.CancellationToken);
            await scheduler.ScheduleAsync(second, TestContext.Current.CancellationToken);
        }

        Assert.Equal(SecurityEmailDeliveryStatus.Queued, (await ReadAsync(services, first)).Status);
        var dropped = await ReadAsync(services, second);
        Assert.Equal(SecurityEmailDeliveryStatus.Dropped, dropped.Status);
        Assert.Equal("queue-full", dropped.ErrorCategory);
        Assert.Null(dropped.SecurityTokenId);
    }

    [Fact]
    public async Task WorkerClaimVersusReconciliation_ExactlyOneWins_AndTheReconcilerNeverMintsOrSends()
    {
        // The planned PoC-15, as an integration test: a stale Queued record raced by the worker and the reconciler.
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = Build(dispatcher);
        const int rounds = 20;
        var workerWins = 0;

        for (var round = 0; round < rounds; round++)
        {
            var userId = await IdentityTestServices.CreateUserAsync(services, emailConfirmed: false);
            var deliveryId = await CreateDeliveryAsync(services, userId);
            await AgeAsync(services, deliveryId, createdBy: TimeSpan.FromMinutes(11), claimedBy: null);
            using var start = new ManualResetEventSlim(false);

            var worker = Task.Run(
                async () =>
                {
                    start.Wait(TestContext.Current.CancellationToken);
                    await using var scope = services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SecurityEmailDeliveryProcessor>().ProcessAsync(deliveryId, TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken);
            var reconciler = Task.Run(
                async () =>
                {
                    start.Wait(TestContext.Current.CancellationToken);
                    await using var scope = services.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<ISecurityEmailDeliveryStore>().ReconcileAsync(StaleAfter, TestContext.Current.CancellationToken);
                },
                TestContext.Current.CancellationToken);
            start.Set();
            await Task.WhenAll(worker, reconciler);

            var delivery = await ReadAsync(services, deliveryId);
            var tokens = await TokenCountAsync(services, userId);
            var handovers = dispatcher.Handovers.Count(h => h.Message.DeliveryId == deliveryId);

            if (delivery.Status == SecurityEmailDeliveryStatus.Sent)
            {
                workerWins++;
                Assert.Equal(1, tokens);
                Assert.Equal(1, handovers);
                Assert.NotNull(delivery.SecurityTokenId);
            }
            else
            {
                Assert.Equal(SecurityEmailDeliveryStatus.Dropped, delivery.Status);
                Assert.Equal(0, tokens);
                Assert.Equal(0, handovers);
                Assert.Null(delivery.SecurityTokenId);
            }
        }

        Assert.InRange(workerWins, 0, rounds);
    }

    [Fact]
    public async Task Reconciliation_IsStatusOnly_StaleQueuedDropped_StaleDispatchingIndeterminate_ClaimedNeverDropped()
    {
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = Build(dispatcher);
        var userId = await IdentityTestServices.CreateUserAsync(services, emailConfirmed: false);

        var staleQueued = await CreateDeliveryAsync(services, userId);
        await AgeAsync(services, staleQueued, TimeSpan.FromMinutes(11), null);

        var freshQueued = await CreateDeliveryAsync(services, userId);

        var staleDispatching = await CreateDeliveryAsync(services, userId);
        Assert.True(await ClaimAsync(services, staleDispatching));
        await AgeAsync(services, staleDispatching, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(11));

        // Created long ago but claimed just now: the worker owns it, so it is not dropped.
        var claimedRecently = await CreateDeliveryAsync(services, userId);
        await AgeAsync(services, claimedRecently, TimeSpan.FromMinutes(30), null);
        Assert.True(await ClaimAsync(services, claimedRecently));

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISecurityEmailDeliveryStore>().ReconcileAsync(StaleAfter, TestContext.Current.CancellationToken);
        }

        Assert.Equal(SecurityEmailDeliveryStatus.Dropped, (await ReadAsync(services, staleQueued)).Status);
        Assert.Equal(SecurityEmailDeliveryStatus.Queued, (await ReadAsync(services, freshQueued)).Status);
        Assert.Equal(SecurityEmailDeliveryStatus.Indeterminate, (await ReadAsync(services, staleDispatching)).Status);
        Assert.Equal(SecurityEmailDeliveryStatus.Dispatching, (await ReadAsync(services, claimedRecently)).Status);
        Assert.Equal(0, await TokenCountAsync(services, userId));
        Assert.Empty(dispatcher.Handovers);
    }

    [Fact]
    public async Task ConcurrentWorkers_ClaimOnce_AndDispatchOnce()
    {
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = Build(dispatcher);
        var userId = await IdentityTestServices.CreateUserAsync(services, emailConfirmed: false);
        var deliveryId = await CreateDeliveryAsync(services, userId);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ProcessAsync(services, deliveryId)));

        Assert.Single(dispatcher.Handovers);
        Assert.Equal(1, await TokenCountAsync(services, userId));
        Assert.Equal(SecurityEmailDeliveryStatus.Sent, (await ReadAsync(services, deliveryId)).Status);
    }

    [Fact]
    public async Task TransientFailures_AreRetried_WithTheSameTokenAndIdentity_UntilDelivered()
    {
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = Build(dispatcher);
        var (userId, email) = await CreateUnconfirmedUserAsync(services);
        dispatcher.Plan(email, attempt => attempt < 3 ? SecurityEmailDispatchOutcome.TransientFailure : SecurityEmailDispatchOutcome.Delivered);
        var deliveryId = await CreateDeliveryAsync(services, userId);

        await ProcessAsync(services, deliveryId);

        var attempts = dispatcher.MessagesTo(email);
        Assert.Equal(3, attempts.Count);
        Assert.Single(attempts.Select(m => m.Link).Distinct());
        Assert.Single(attempts.Select(m => m.DeliveryId).Distinct());
        Assert.Equal(1, await TokenCountAsync(services, userId));
        var delivery = await ReadAsync(services, deliveryId);
        Assert.Equal(SecurityEmailDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(3, delivery.AttemptCount);
    }

    [Theory]
    [InlineData(SecurityEmailDispatchOutcome.TransientFailure, SecurityEmailDeliveryStatus.Failed, 3, "retries-exhausted")]
    [InlineData(SecurityEmailDispatchOutcome.Unknown, SecurityEmailDeliveryStatus.Indeterminate, 3, "retries-exhausted-unknown")]
    [InlineData(SecurityEmailDispatchOutcome.PermanentFailure, SecurityEmailDeliveryStatus.Failed, 1, "rejected")]
    public async Task UnsuccessfulHandovers_AreBounded_AndRecorded(
        SecurityEmailDispatchOutcome outcome,
        SecurityEmailDeliveryStatus expectedStatus,
        int expectedAttempts,
        string expectedCategory)
    {
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = Build(dispatcher);
        var (userId, email) = await CreateUnconfirmedUserAsync(services);
        dispatcher.Plan(email, _ => outcome);
        var deliveryId = await CreateDeliveryAsync(services, userId);

        await ProcessAsync(services, deliveryId);

        var delivery = await ReadAsync(services, deliveryId);
        Assert.Equal(expectedStatus, delivery.Status);
        Assert.Equal(expectedAttempts, delivery.AttemptCount);
        Assert.Equal(expectedCategory, delivery.ErrorCategory);
        Assert.Equal(expectedAttempts, dispatcher.MessagesTo(email).Count);
        Assert.Equal(1, await TokenCountAsync(services, userId));
    }

    [Fact]
    public async Task ThrowingDispatcher_IsTreatedAsAnUnknownOutcome()
    {
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = Build(dispatcher);
        var (userId, email) = await CreateUnconfirmedUserAsync(services);
        dispatcher.Plan(email, _ => throw new HttpRequestException("simulated failure after sending"));
        var deliveryId = await CreateDeliveryAsync(services, userId);

        await ProcessAsync(services, deliveryId);

        Assert.Equal(SecurityEmailDeliveryStatus.Indeterminate, (await ReadAsync(services, deliveryId)).Status);
    }

    [Fact]
    public async Task ConfirmedAccount_GetsNoToken_AndNoEmail()
    {
        database.RequireAvailable();
        var dispatcher = new CapturingSecurityEmailDispatcher();
        await using var services = Build(dispatcher);
        var userId = await IdentityTestServices.CreateUserAsync(services, emailConfirmed: true);
        var deliveryId = await CreateDeliveryAsync(services, userId);

        await ProcessAsync(services, deliveryId);

        var delivery = await ReadAsync(services, deliveryId);
        Assert.Equal(SecurityEmailDeliveryStatus.Failed, delivery.Status);
        Assert.Equal("not-applicable", delivery.ErrorCategory);
        Assert.Empty(dispatcher.Handovers);
        Assert.Equal(0, await TokenCountAsync(services, userId));
    }

    [Fact]
    public async Task WithoutTheDevelopmentMailSink_TheNotificationIntegrationIsTheSingleDispatcher()
    {
        database.RequireAvailable();
        await using var services = IdentityTestServices.Build(database);

        var dispatchers = services.GetServices<ISecurityEmailDispatcher>().ToList();

        Assert.Equal("NotificationSecurityEmailDispatcher", Assert.Single(dispatchers).GetType().Name);
    }

    private ServiceProvider Build(CapturingSecurityEmailDispatcher dispatcher, IDictionary<string, string?>? settings = null) =>
        IdentityTestServices.Build(database, settings, services =>
        {
            services.RemoveAll<ISecurityEmailDispatcher>();
            services.AddSingleton<ISecurityEmailDispatcher>(dispatcher);
        });

    private static async Task<(string UserId, string Email)> CreateUnconfirmedUserAsync(IServiceProvider services)
    {
        var userId = await IdentityTestServices.CreateUserAsync(services, emailConfirmed: false);
        await using var scope = services.CreateAsyncScope();
        var email = await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().Users
            .Where(u => u.Id == userId).Select(u => u.Email!).SingleAsync(TestContext.Current.CancellationToken);
        return (userId, email);
    }

    private static Task<Guid> CreateDeliveryAsync(IServiceProvider services, string userId) =>
        services.GetRequiredService<IAuthoritativeSecurityTransaction>().ExecuteAsync(
            "test-create-delivery",
            async (unit, ct) => TransactionDecision.Commit(await unit.CreateSecurityEmailDeliveryAsync(userId, SecurityTokenPurpose.EmailConfirmation, ct)),
            TestContext.Current.CancellationToken);

    private static async Task ProcessAsync(IServiceProvider services, Guid deliveryId)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SecurityEmailDeliveryProcessor>().ProcessAsync(deliveryId, TestContext.Current.CancellationToken);
    }

    private static async Task<bool> ClaimAsync(IServiceProvider services, Guid deliveryId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISecurityEmailDeliveryStore>().TryClaimAsync(deliveryId, TestContext.Current.CancellationToken);
    }

    private static async Task AgeAsync(IServiceProvider services, Guid deliveryId, TimeSpan createdBy, TimeSpan? claimedBy)
    {
        await using var scope = services.CreateAsyncScope();
        var deliveries = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().SecurityEmailDeliveries.Where(d => d.Id == deliveryId);
        await deliveries.ExecuteUpdateAsync(set => set.SetProperty(d => d.CreatedAt, d => d.CreatedAt.AddSeconds(-createdBy.TotalSeconds)), TestContext.Current.CancellationToken);
        if (claimedBy is { } claimedAge)
        {
            await deliveries.ExecuteUpdateAsync(set => set.SetProperty(d => d.ClaimedAt, d => d.ClaimedAt!.Value.AddSeconds(-claimedAge.TotalSeconds)), TestContext.Current.CancellationToken);
        }
    }

    private static async Task<SecurityEmailDeliveryRecord> ReadAsync(IServiceProvider services, Guid deliveryId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().SecurityEmailDeliveries
            .AsNoTracking().SingleAsync(d => d.Id == deliveryId, TestContext.Current.CancellationToken);
    }

    private static async Task<int> TokenCountAsync(IServiceProvider services, string userId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>().AccountSecurityTokens
            .CountAsync(t => t.UserId == userId, TestContext.Current.CancellationToken);
    }
}
