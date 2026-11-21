using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure;

/// <summary>Registers the Infrastructure-layer implementations: the service's own SQL Server persistence.</summary>
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

        services.AddDbContext<TenantMerchantDbContext>((provider, options) =>
            options.UseSqlServer(provider.GetRequiredService<IOptions<TenantMerchantDatabaseOptions>>().Value.ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ITenantProvisioningStore, TenantProvisioningStore>();
        services.AddScoped<ITenantReadStore, TenantReadStore>();
        return services;
    }
}
