using EnterpriseCommerce.Identity.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Identity.Infrastructure.Retention;

/// <summary>
/// The hosted retention worker: after <see cref="IdentityRetentionOptions.InitialDelay"/>, one run every
/// <see cref="IdentityRetentionOptions.Interval"/>. Runs of one instance are sequential and never overlap; instances
/// coordinate through the runner's application lock. A failed run is logged and the next tick tries again.
/// </summary>
internal sealed partial class IdentityRetentionWorker(
    IdentityRetentionRunner runner,
    IOptions<IdentityRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<IdentityRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        LogStarted(logger, settings.ReplayProtectionWindow, settings.Interval);
        await Task.Delay(settings.InitialDelay, timeProvider, stoppingToken);

        using var timer = new PeriodicTimer(settings.Interval, timeProvider);
        do
        {
            try
            {
                await runner.RunOnceAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRunFailed(logger, exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(EventId = 2210, Level = LogLevel.Warning, Message = "Retention worker disabled by configuration")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 2211, Level = LogLevel.Information, Message = "Retention worker started: replay protection window {Window}, interval {Interval}")]
    private static partial void LogStarted(ILogger logger, TimeSpan window, TimeSpan interval);

    [LoggerMessage(EventId = 2212, Level = LogLevel.Error, Message = "Retention run failed ({ExceptionType}); retrying at the next interval")]
    private static partial void LogRunFailed(ILogger logger, string exceptionType);
}

/// <summary>Registration of the retention cleanup.</summary>
public static class RetentionServiceCollectionExtensions
{
    internal static IServiceCollection AddIdentityRetention(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.AddOptions<IdentityRetentionOptions>()
            .Bind(configuration.GetSection(IdentityRetentionOptions.SectionName))
            .Validate(
                options => options.IsValid(),
                $"{IdentityRetentionOptions.SectionName} is out of range, or the replay protection window was not computed by the host.")
            .ValidateOnStart();

        services.AddSingleton<IdentityRetentionRunner>();
        services.AddHostedService<IdentityRetentionWorker>();
        return services;
    }

    /// <summary>
    /// Sets the replay protection window from every configured token and session lifetime (the host knows them:
    /// they are client registrations). Startup fails if this is never called.
    /// </summary>
    public static IServiceCollection UseReplayProtectionLifetimes(this IServiceCollection services, IReadOnlyCollection<TimeSpan> tokenLifetimes)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(tokenLifetimes);

        services.PostConfigure<IdentityRetentionOptions>(options =>
            options.ReplayProtectionWindow = ReplayProtectionWindow.Compute(tokenLifetimes, options.SafetyMargin, options.MinimumReplayProtectionWindow));
        return services;
    }
}
