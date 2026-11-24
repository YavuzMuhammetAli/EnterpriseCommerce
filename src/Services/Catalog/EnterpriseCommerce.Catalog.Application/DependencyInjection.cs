using EnterpriseCommerce.Catalog.Application.Authorization;
using EnterpriseCommerce.Catalog.Application.Brands;
using EnterpriseCommerce.Catalog.Application.Categories;
using EnterpriseCommerce.Catalog.Application.Discovery;
using EnterpriseCommerce.Catalog.Application.Products;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Catalog.Application;

/// <summary>Registers the Application-layer use cases.</summary>
public static class DependencyInjection
{
    /// <summary>Handlers are plain classes resolved from DI; no mediator is used (CLAUDE.md, ADR-009).</summary>
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<CatalogAccessAuthorizer>();
        services.AddScoped<ProductReferences>();
        services.AddScoped<CreateProductHandler>();
        services.AddScoped<UpdateProductHandler>();
        services.AddScoped<ReplaceProductMediaHandler>();
        services.AddScoped<ProductLifecycleHandler>();
        services.AddScoped<MerchantProductQueryHandler>();
        services.AddScoped<VariantVerificationHandler>();
        services.AddScoped<BrandHandlers>();
        services.AddScoped<CategoryHandlers>();
        services.AddScoped<PublicCatalogHandler>();
        return services;
    }
}
