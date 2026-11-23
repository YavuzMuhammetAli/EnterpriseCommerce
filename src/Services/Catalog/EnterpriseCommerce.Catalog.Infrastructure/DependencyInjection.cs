using EnterpriseCommerce.Catalog.Application.Authorization;
using EnterpriseCommerce.Catalog.Application.Brands;
using EnterpriseCommerce.Catalog.Application.Categories;
using EnterpriseCommerce.Catalog.Application.Discovery;
using EnterpriseCommerce.Catalog.Application.Products;
using EnterpriseCommerce.Catalog.Infrastructure.Membership;
using EnterpriseCommerce.Catalog.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Catalog.Infrastructure;

/// <summary>Registers the Catalog infrastructure: MongoDB persistence and the Tenant &amp; Merchant client.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(TimeProvider.System);

        services.AddOptions<CatalogDatabaseOptions>()
            .Bind(configuration.GetSection(CatalogDatabaseOptions.SectionName))
            .Configure(options => options.ConnectionString = configuration.GetConnectionString(CatalogDatabaseOptions.ConnectionStringName))
            .Validate(
                options => !options.Validate().Any(),
                "Invalid Catalog database configuration: check ConnectionStrings:CatalogDatabase and the Catalog:Database section.")
            .ValidateOnStart();

        services.AddSingleton(provider => new CatalogMongoContext(provider.GetRequiredService<IOptions<CatalogDatabaseOptions>>().Value));
        services.AddSingleton<CatalogIndexState>();
        services.AddHostedService<CatalogIndexInitializer>();
        services.AddSingleton<IProductStore, ProductStore>();
        services.AddSingleton<ICategoryStore, CategoryStore>();
        services.AddSingleton<IBrandStore, BrandStore>();
        services.AddSingleton<ICatalogQueries, CatalogQueries>();

        AddMembershipVerification(services, configuration);
        return services;
    }

    private static void AddMembershipVerification(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MembershipVerificationOptions>()
            .Bind(configuration.GetSection(MembershipVerificationOptions.SectionName))
            .PostConfigure(options => options.Issuer ??= Uri.TryCreate(configuration["Catalog:Authentication:Issuer"], UriKind.Absolute, out var issuer) ? issuer : null)
            .Validate<IHostEnvironment>(
                (options, environment) => !options.Validate(environment.IsDevelopment()).Any(),
                $"{MembershipVerificationOptions.SectionName} is invalid (ADR-025): see CATALOG_OPERATIONS.md for the required values.")
            .ValidateOnStart();

        services.AddSingleton<CatalogServiceTokenProvider>();
        services.AddHttpClient(CatalogServiceTokenProvider.HttpClientName, (provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<MembershipVerificationOptions>>().Value.Timeout);
        services.AddHttpClient<IMembershipVerifier, TenantMerchantMembershipVerifier>((provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<MembershipVerificationOptions>>().Value.Timeout);
    }
}
