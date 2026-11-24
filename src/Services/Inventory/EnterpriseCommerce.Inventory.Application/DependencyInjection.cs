using EnterpriseCommerce.Inventory.Application.Authorization;
using EnterpriseCommerce.Inventory.Application.Items;
using EnterpriseCommerce.Inventory.Application.Reservations;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Inventory.Application;

/// <summary>Registers the Application-layer use cases.</summary>
public static class DependencyInjection
{
    /// <summary>Handlers are plain classes resolved from DI; no mediator is used (CLAUDE.md, ADR-009).</summary>
    public static IServiceCollection AddInventoryApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<InventoryAccessAuthorizer>();
        services.AddScoped<CreateInventoryItemHandler>();
        services.AddScoped<AdjustStockHandler>();
        services.AddScoped<InventoryQueryHandler>();
        services.AddScoped<CreateReservationHandler>();
        services.AddScoped<ReservationHandlers>();
        return services;
    }
}
