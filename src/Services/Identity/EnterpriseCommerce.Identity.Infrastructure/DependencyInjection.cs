using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.Infrastructure;

/// <summary>
/// Registers Infrastructure-layer implementations (persistence, external adapters).
/// Persistence and messaging are not wired yet; they arrive with the Identity design phase.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services;
    }
}
