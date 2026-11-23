using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Catalog.Application;

/// <summary>Registers the Application-layer use cases.</summary>
public static class DependencyInjection
{
    /// <summary>Handlers are plain classes resolved from DI; no mediator is used (CLAUDE.md, ADR-009).</summary>
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
