using EnterpriseCommerce.Identity.Application.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.Application;

/// <summary>
/// Registers Application-layer services (use case handlers).
/// Handlers are plain classes resolved from DI; no mediator is used (CLAUDE.md, ADR-009).
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<LogoutAllSessionsHandler>();

        return services;
    }
}
