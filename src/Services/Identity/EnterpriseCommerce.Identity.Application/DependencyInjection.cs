using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.Application;

/// <summary>
/// Registers Application-layer services (use case handlers, validators).
/// No use cases exist yet; this is the composition seam the API calls into.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services;
    }
}
