using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Cart.Application;

/// <summary>Registers the Application-layer use cases. No mediator is used (CLAUDE.md, ADR-009).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddCartApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
