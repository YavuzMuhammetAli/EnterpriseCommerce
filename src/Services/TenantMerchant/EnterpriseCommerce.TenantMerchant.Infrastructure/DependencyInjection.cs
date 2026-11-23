using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Application.Onboarding;
using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Identity;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure;

/// <summary>
/// Registers the Infrastructure-layer implementations: the service's own SQL Server persistence, the Identity
/// verification client, and the outbox relay.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddTenantMerchantInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read lazily, so that hosts and tests can supply the value through any configuration source.
        // Never committed: set ConnectionStrings__TenantMerchantDatabase in the environment or user secrets.
        services.AddOptions<TenantMerchantDatabaseOptions>()
            .Configure(options => options.ConnectionString = configuration.GetConnectionString(TenantMerchantDatabaseOptions.ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"ConnectionStrings:{TenantMerchantDatabaseOptions.ConnectionStringName} is required.")
            .ValidateOnStart();

        // The outbox settings: the topic of every new row, and the relay (ADR-023 section 5).
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            .Validate(options => !options.Validate().Any(), $"{OutboxOptions.SectionName} is invalid: see PHASE_3_S4_EVENT_FLOW.md section 5 for the allowed values.")
            .ValidateOnStart();

        services.AddDbContext<TenantMerchantDbContext>((provider, options) =>
            options.UseSqlServer(provider.GetRequiredService<IOptions<TenantMerchantDatabaseOptions>>().Value.ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ITenantProvisioningStore, TenantProvisioningStore>();
        services.AddScoped<ITenantReadStore, TenantReadStore>();
        services.AddScoped<IInvitationReadStore, InvitationReadStore>();
        services.AddSingleton<ITenantWriteGate, TenantWriteGate>();
        services.AddSingleton<ITenantOnboardingGate, TenantOnboardingGate>();

        services.AddOptions<InvitationSettings>()
            .Bind(configuration.GetSection(InvitationSettings.SectionName))
            .Validate(settings => !settings.Validate().Any(), $"{InvitationSettings.SectionName} is invalid: Lifetime 1 hour to 30 days, MaxOpenPerTenant 1 to 1000.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<InvitationSettings>>().Value);

        services.AddOptions<OnboardingSettings>()
            .Bind(configuration.GetSection(OnboardingSettings.SectionName))
            .Validate(settings => !settings.Validate().Any(), $"{OnboardingSettings.SectionName} is invalid: MaxTenantsPerSubject 1 to 10.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<OnboardingSettings>>().Value);

        AddIdentityVerification(services, configuration);
        AddOutboxRelay(services, configuration);
        return services;
    }

    /// <summary>
    /// The relay that publishes committed outbox rows to Kafka (ADR-023 sections 5 and 6). Outside Development the
    /// service refuses to start without a broker, or with a connection that is not mutual TLS. In Development without
    /// a broker the relay is disabled with a warning, and committed events stay pending.
    /// </summary>
    private static void AddOutboxRelay(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<KafkaOptions>()
            .Bind(configuration.GetSection(KafkaOptions.SectionName))
            .Validate<IHostEnvironment>(
                (options, environment) => !options.Validate(environment.IsDevelopment()).Any(),
                $"{KafkaOptions.SectionName} is invalid (ADR-023 section 6): BootstrapServers is required outside Development, with SecurityProtocol Ssl and a client certificate and key; MessageTimeout 1 second to 5 minutes; every named file must exist.")
            .ValidateOnStart();

        services.AddSingleton(_ =>
        {
            var state = new OutboxRelayState();
            OutboxMetrics.Observe(state);
            return state;
        });
        services.AddSingleton<IOutboxStore, SqlOutboxStore>();
        services.AddSingleton<IOutboxPublisher, KafkaOutboxPublisher>();
        services.AddSingleton<IOutboxLeadership, SqlOutboxLeadership>();
        services.AddHostedService<OutboxRelay>();
    }

    private static void AddIdentityVerification(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IdentityVerificationOptions>()
            .Bind(configuration.GetSection(IdentityVerificationOptions.SectionName))
            .PostConfigure(options => options.Issuer ??= Uri.TryCreate(configuration["TenantMerchant:Authentication:Issuer"], UriKind.Absolute, out var issuer) ? issuer : null)
            .Validate<IHostEnvironment>(
                (options, environment) => !options.Validate(environment.IsDevelopment()).Any(),
                $"{IdentityVerificationOptions.SectionName} is invalid (ADR-021): see TENANT_MERCHANT_API_CONTRACT.md for the required values.")
            .ValidateOnStart();

        services.AddSingleton<IdentityServiceTokenProvider>();
        services.AddHttpClient(IdentityServiceTokenProvider.HttpClientName, (provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<IdentityVerificationOptions>>().Value.Timeout);
        services.AddHttpClient<IIdentityAccountVerifier, IdentityAccountVerifier>((provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<IdentityVerificationOptions>>().Value.Timeout);
    }
}
