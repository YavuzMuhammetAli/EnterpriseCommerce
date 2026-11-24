using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Infrastructure.Messaging;
using EnterpriseCommerce.Inventory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Inventory.Infrastructure;

/// <summary>
/// Registers the Infrastructure-layer implementations: the service's own SQL Server persistence and the transactional
/// outbox with its relay.
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

        // The outbox settings, the topic of every new row, and the relay that publishes committed rows to Kafka
        // (ADR-023 sections 5 and 6, ADR-028), from the shared block.
        services.AddSqlServerOutboxRelay<InventoryDbContext>(
            configuration,
            InventoryOutbox.Profile,
            provider => provider.GetRequiredService<IOptions<InventoryDatabaseOptions>>().Value.ConnectionString);
        return services;
    }
}
