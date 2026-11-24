using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.Inventory.Application.Authorization;
using EnterpriseCommerce.Inventory.Application.Catalog;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Application.Items;
using EnterpriseCommerce.Inventory.Infrastructure.Messaging;
using EnterpriseCommerce.Inventory.Infrastructure.Persistence;
using EnterpriseCommerce.Inventory.Infrastructure.ServiceClients;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.Infrastructure;

/// <summary>
/// Registers the Infrastructure-layer implementations: the service's own SQL Server persistence, the transactional
/// outbox with its relay, and the clients of Tenant &amp; Merchant's and Catalog's internal verifications.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInventoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read lazily, so that hosts and tests can supply the value through any configuration source.
        // Never committed: set ConnectionStrings__InventoryDatabase in the environment or user secrets.
        services.AddOptions<InventoryDatabaseOptions>()
            .Configure(options => options.ConnectionString = configuration.GetConnectionString(InventoryDatabaseOptions.ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"ConnectionStrings:{InventoryDatabaseOptions.ConnectionStringName} is required.")
            .ValidateOnStart();

        services.AddDbContext<InventoryDbContext>((provider, options) =>
            options.UseSqlServer(provider.GetRequiredService<IOptions<InventoryDatabaseOptions>>().Value.ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IInventoryWriteGate, InventoryWriteGate>();
        services.AddScoped<IInventoryQueries, InventoryQueries>();
        services.AddScoped<InventoryDatabaseHealthCheck>();

        // The outbox settings, the topic of every new row, and the relay that publishes committed rows to Kafka
        // (ADR-023 sections 5 and 6, ADR-028), from the shared block.
        services.AddSqlServerOutboxRelay<InventoryDbContext>(
            configuration,
            InventoryOutbox.Profile,
            provider => provider.GetRequiredService<IOptions<InventoryDatabaseOptions>>().Value.ConnectionString);

        AddServiceClients(services, configuration);
        return services;
    }

    /// <summary>
    /// The <c>svc-inventory</c> client and the internal APIs it calls: Tenant &amp; Merchant's membership verification for
    /// every merchant request (ADR-025) and Catalog's variant verification for item creation (ADR-027). Outside
    /// Development all three are required; in Development an unconfigured call fails closed with <c>503</c>.
    /// </summary>
    private static void AddServiceClients(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ServiceClientOptions>()
            .Bind(configuration.GetSection(ServiceClientOptions.SectionName))
            .PostConfigure(options => options.Issuer ??= Uri.TryCreate(configuration["Inventory:Authentication:Issuer"], UriKind.Absolute, out var issuer) ? issuer : null)
            .Validate<IHostEnvironment>(
                (options, environment) => !options.Validate(environment.IsDevelopment()).Any(),
                $"{ServiceClientOptions.SectionName} is invalid (ADR-025, ADR-027): see INVENTORY_OPERATIONS.md section 2 for the required values.")
            .ValidateOnStart();
        AddEndpointOptions<MembershipVerificationOptions>(services, configuration, MembershipVerificationOptions.Section);
        AddEndpointOptions<CatalogVerificationOptions>(services, configuration, CatalogVerificationOptions.Section);

        services.AddSingleton<InventoryServiceTokenProvider>();
        services.AddHttpClient(InventoryServiceTokenProvider.HttpClientName, (provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<ServiceClientOptions>>().Value.Timeout);
        services.AddHttpClient<IMembershipVerifier, TenantMerchantMembershipVerifier>((provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<MembershipVerificationOptions>>().Value.Timeout);
        services.AddHttpClient<ICatalogVariantVerifier, CatalogVariantVerifier>((provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<CatalogVerificationOptions>>().Value.Timeout);
    }

    private static void AddEndpointOptions<TOptions>(IServiceCollection services, IConfiguration configuration, string section)
        where TOptions : ServiceEndpointOptions =>
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(section))
            // The service client's own section, unvalidated: its errors are reported by its own validation, and every
            // endpoint's errors are reported too, instead of the first failing options hiding the others.
            .Validate<IHostEnvironment>(
                (options, environment) => !options.Validate(
                    environment.IsDevelopment(),
                    (configuration.GetSection(ServiceClientOptions.SectionName).Get<ServiceClientOptions>() ?? new ServiceClientOptions()).IsSpecified).Any(),
                $"{section} is invalid (ADR-025, ADR-027): see INVENTORY_OPERATIONS.md section 2 for the required values.")
            .ValidateOnStart();
}
