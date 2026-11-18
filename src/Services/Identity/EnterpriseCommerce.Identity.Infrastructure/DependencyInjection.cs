using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Identity.Infrastructure;

/// <summary>
/// Registers Infrastructure-layer implementations: Identity's SQL Server persistence, the OpenIddict core
/// stores, and the authoritative security transaction with its post-commit cleanup.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read lazily, so that hosts and tests can supply the value through any configuration source.
        // Never committed: set ConnectionStrings__IdentityDatabase in the environment or user secrets.
        services.AddOptions<IdentityDatabaseOptions>()
            .Configure(options => options.ConnectionString = configuration.GetConnectionString(IdentityDatabaseOptions.ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"ConnectionStrings:{IdentityDatabaseOptions.ConnectionStringName} is required.")
            .ValidateOnStart();

        services.AddOptions<SecurityTransactionOptions>()
            .Bind(configuration.GetSection(SecurityTransactionOptions.SectionName))
            .Validate(
                options => options.MaxAttempts is >= 1 and <= 10
                    && options.CleanupMaxAttempts is >= 1 and <= 10
                    && options.BaseDelay > TimeSpan.Zero
                    && options.BaseDelay <= TimeSpan.FromSeconds(1),
                $"{SecurityTransactionOptions.SectionName} is out of range.")
            .ValidateOnStart();

        services.AddDbContext<IdentityServiceDbContext>((provider, options) =>
        {
            options.UseSqlServer(provider.GetRequiredService<IOptions<IdentityDatabaseOptions>>().Value.ConnectionString);
            options.UseOpenIddict();
        });

        services.AddOpenIddict()
            .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<IdentityServiceDbContext>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuthoritativeSecurityTransaction, AuthoritativeSecurityTransaction>();
        services.AddSingleton<ISessionRevocationCleanup, SessionRevocationCleanup>();

        return services;
    }
}
