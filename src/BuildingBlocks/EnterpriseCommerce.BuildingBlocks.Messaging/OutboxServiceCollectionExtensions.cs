using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>Registers one service's transactional outbox and its Kafka relay (ADR-023 sections 5 and 6, ADR-028 section 4).</summary>
public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the profile and the relay's settings, both validated at startup: <see cref="OutboxOptions"/> (with the
    /// profile's default topic) and <see cref="KafkaOptions"/>. It also registers the relay with its SQL Server store and
    /// leadership on <typeparamref name="TContext"/>.
    /// </summary>
    /// <remarks>
    /// Outside Development the service refuses to start without a broker, or with a connection that is not mutual TLS. In
    /// Development without a broker the relay is disabled with a warning, and committed events stay pending.
    /// </remarks>
    /// <typeparam name="TContext">The service's <c>DbContext</c>, whose model calls <see cref="OutboxModel.MapOutboxMessages"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration holding the profile's sections.</param>
    /// <param name="profile">The service's profile.</param>
    /// <param name="connectionString">Reads the service database's connection string, for the leadership session.</param>
    public static IServiceCollection AddSqlServerOutboxRelay<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        OutboxRelayProfile profile,
        Func<IServiceProvider, string?> connectionString)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(connectionString);

        var problems = profile.Validate().ToList();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"The outbox relay profile is invalid: {string.Join(" ", problems)}", nameof(profile));
        }

        services.AddSingleton(profile);

        // The topic of every new row, and the relay (ADR-023 section 5).
        services.AddOptions<OutboxOptions>()
            .Configure(options => options.Topic = profile.DefaultTopic)
            .Bind(configuration.GetSection(profile.OutboxSection))
            .Validate(options => !options.Validate().Any(), $"{profile.OutboxSection} is invalid: see {profile.SettingsReference} for the allowed values.")
            .ValidateOnStart();

        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(profile.KafkaSection))
            .Validate<IHostEnvironment>(
                (options, environment) => !options.Validate(environment.IsDevelopment()).Any(),
                $"{profile.KafkaSection} is invalid (ADR-023 section 6): BootstrapServers is required outside Development, with SecurityProtocol Ssl and a client certificate and key; MessageTimeout 1 second to 5 minutes; every named file must exist.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ =>
        {
            var metrics = OutboxMetrics.For(profile.MeterName);
            var state = new OutboxRelayState(metrics);
            metrics.Observe(state);
            return state;
        });
        services.AddSingleton<IOutboxStore, SqlOutboxStore<TContext>>();
        services.AddSingleton<IOutboxPublisher, KafkaOutboxPublisher>();
        services.AddSingleton<IOutboxLeadership>(provider => new SqlOutboxLeadership(() => connectionString(provider), profile.LockApplicationName));
        services.AddHostedService<OutboxRelay>();
        return services;
    }
}
