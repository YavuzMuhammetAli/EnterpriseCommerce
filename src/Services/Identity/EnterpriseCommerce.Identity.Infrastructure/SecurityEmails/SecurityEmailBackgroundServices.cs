using System.Threading.Channels;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Infrastructure.Diagnostics;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;

/// <summary>
/// The bounded in-process wake-up channel (ADR-018 section 3). It carries opaque delivery IDs only. Writing never
/// blocks: a full channel returns <see langword="false"/>, and the caller records the delivery as dropped.
/// </summary>
internal sealed partial class SecurityEmailQueue(IOptions<SecurityEmailDeliveryOptions> options, ILogger<SecurityEmailQueue> logger) : ISecurityEmailQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(options.Value.QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false,
    });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public bool TryEnqueue(Guid deliveryId)
    {
        if (_channel.Writer.TryWrite(deliveryId))
        {
            return true;
        }

        IdentityMetrics.SecurityEmailQueueSaturated.Add(1);
        LogSaturated(logger, options.Value.QueueCapacity);
        return false;
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Error, Message = "Security email queue saturated at capacity {Capacity}")]
    private static partial void LogSaturated(ILogger logger, int capacity);
}

/// <summary>
/// The single hosted delivery worker: reads delivery IDs and processes up to
/// <see cref="SecurityEmailDeliveryOptions.MaxConcurrentDispatches"/> of them at once, each in its own scope.
/// </summary>
internal sealed partial class SecurityEmailDeliveryWorker(
    SecurityEmailQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityEmailDeliveryOptions> options,
    ILogger<SecurityEmailDeliveryWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Parallel.ForEachAsync(
            queue.Reader.ReadAllAsync(stoppingToken),
            new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxConcurrentDispatches, CancellationToken = stoppingToken },
            async (deliveryId, cancellationToken) =>
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<SecurityEmailDeliveryProcessor>().ProcessAsync(deliveryId, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The record stays in its last committed status; reconciliation makes a stuck one visible.
                    LogProcessingFailed(logger, exception, deliveryId);
                }
            });

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error, Message = "Security email delivery {DeliveryId} could not be processed")]
    private static partial void LogProcessingFailed(ILogger logger, Exception exception, Guid deliveryId);
}

/// <summary>
/// Status-only reconciliation, at startup and every <see cref="SecurityEmailDeliveryOptions.ReconcileInterval"/>
/// (ADR-018 section 3). It never resends and never mints a token.
/// </summary>
internal sealed partial class SecurityEmailReconciler(
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityEmailDeliveryOptions> options,
    BackgroundWorkerHeartbeats heartbeats,
    TimeProvider timeProvider,
    ILogger<SecurityEmailReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ReconcileInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISecurityEmailDeliveryStore>()
                    .ReconcileAsync(options.Value.StaleAfter, stoppingToken);
                if (result.Dropped > 0 || result.Indeterminate > 0)
                {
                    LogReconciled(logger, result.Dropped, result.Indeterminate);
                }

                heartbeats.SecurityEmailReconciliation.Record(succeeded: true, timeProvider.GetUtcNow());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                heartbeats.SecurityEmailReconciliation.Record(succeeded: false, timeProvider.GetUtcNow());
                LogReconciliationFailed(logger, exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(EventId = 2110, Level = LogLevel.Warning, Message = "Security email reconciliation: {Dropped} stale queued dropped, {Indeterminate} stale dispatching indeterminate")]
    private static partial void LogReconciled(ILogger logger, int dropped, int indeterminate);

    [LoggerMessage(EventId = 2111, Level = LogLevel.Error, Message = "Security email reconciliation failed ({ExceptionType}); retrying at the next interval")]
    private static partial void LogReconciliationFailed(ILogger logger, string exceptionType);
}
