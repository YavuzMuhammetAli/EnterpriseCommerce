using EnterpriseCommerce.Cart.Application.Carts;
using EnterpriseCommerce.Cart.Application.Common;
using EnterpriseCommerce.Cart.Application.Catalog;
using EnterpriseCommerce.Cart.Infrastructure.Caching;
using EnterpriseCommerce.Cart.Infrastructure.Catalog;
using EnterpriseCommerce.Cart.Infrastructure.Cleanup;
using EnterpriseCommerce.Cart.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Cart.Infrastructure;

/// <summary>
/// Registers the Infrastructure-layer implementations: the service's own SQL Server persistence, the expired-cart
/// cleanup, the cache Redis connection and the Catalog product read (ADR-029, ADR-030).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCartInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read lazily, so that hosts and tests can supply the value through any configuration source.
        // Never committed: set ConnectionStrings__CartDatabase in the environment or user secrets.
        services.AddOptions<CartDatabaseOptions>()
            .Configure(options => options.ConnectionString = configuration.GetConnectionString(CartDatabaseOptions.ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"ConnectionStrings:{CartDatabaseOptions.ConnectionStringName} is required.")
            .ValidateOnStart();

        services.AddDbContext<CartDbContext>((provider, options) =>
            options.UseSqlServer(provider.GetRequiredService<IOptions<CartDatabaseOptions>>().Value.ConnectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICartStore, CartStore>();
        services.AddScoped<ICartQueries, CartQueries>();
        services.AddScoped<CartDatabaseHealthCheck>();

        AddSettings<CartLifecycleSettings>(services, configuration, CartLifecycleSettings.SectionName, settings => settings.Validate());
        AddSettings<CartPricingSettings>(services, configuration, CartPricingSettings.SectionName, settings => settings.Validate());

        // The expired-cart cleanup on every instance (ADR-029 section 9); multi-instance safe without a leader.
        services.AddOptions<CartCleanupOptions>()
            .Bind(configuration.GetSection(CartCleanupOptions.SectionName))
            .Validate(options => !options.Validate().Any(), $"{CartCleanupOptions.SectionName} is invalid: see CART_OPERATIONS.md section 2 for the allowed values.")
            .ValidateOnStart();
        services.AddSingleton<CartCleanupState>();
        services.AddSingleton<CartCleanupRunner>();
        services.AddSingleton<CartCleanupHealthCheck>();
        services.AddHostedService<CartCleanupWorker>();

        AddCache(services, configuration);
        AddCatalog(services, configuration);
        return services;
    }

    /// <summary>
    /// Catalog's public product read (ADR-029 section 5): anonymous, so no service client is involved. Outside Development
    /// the address is required; in Development an unconfigured read fails closed with <c>503</c>.
    /// </summary>
    private static void AddCatalog(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CatalogReadOptions>()
            .Bind(configuration.GetSection(CatalogReadOptions.SectionName))
            .Validate<IHostEnvironment>(
                (options, environment) => !options.Validate(environment.IsDevelopment()).Any(),
                $"{CatalogReadOptions.SectionName} is invalid (ADR-029): see CART_OPERATIONS.md section 2 for the required values.")
            .ValidateOnStart();
        services.AddHttpClient<ICatalogProductReader, CatalogProductReader>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<CatalogReadOptions>>().Value;
            client.Timeout = options.Timeout;
            client.MaxResponseContentBufferSize = options.MaxResponseBytes;
        });
    }

    private static void AddSettings<TSettings>(IServiceCollection services, IConfiguration configuration, string section, Func<TSettings, IEnumerable<string>> validate)
        where TSettings : class
    {
        services.AddOptions<TSettings>()
            .Bind(configuration.GetSection(section))
            .Validate(settings => !validate(settings).Any(), $"{section} is invalid: see CART_OPERATIONS.md section 2 for the allowed values.")
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<TSettings>>().Value);
    }

    /// <summary>The cache Redis (ADR-030): its settings, connection and health check.</summary>
    private static void AddCache(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CartCacheOptions>()
            .Bind(configuration.GetSection(CartCacheOptions.SectionName))
            .Configure(options => options.ConnectionString = configuration.GetConnectionString(CartCacheOptions.ConnectionStringName))
            .Validate(options => !options.Validate().Any(), $"{CartCacheOptions.SectionName} is invalid (ADR-030): see CART_OPERATIONS.md section 2 for the allowed values.")
            .ValidateOnStart();
        services.AddSingleton<CartCacheConnection>();
        services.AddSingleton<CartCacheHealthCheck>();
        services.AddSingleton<ICartCache, NoCartCache>();
    }
}
