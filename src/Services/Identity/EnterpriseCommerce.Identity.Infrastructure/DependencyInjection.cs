using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.Infrastructure.Security;
using EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;
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

        // ASP.NET Core Identity user store only: no roles (ADR-015 A3) and no framework email or reset tokens,
        // which are replaced by persisted single-use tokens (ADR-018).
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // The user name is the validated email address (AccountEmail); no second character filter.
                options.User.AllowedUserNameCharacters = string.Empty;
                options.SignIn.RequireConfirmedEmail = true;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Password.RequiredLength = 12;
            })
            .AddEntityFrameworkStores<IdentityServiceDbContext>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IAuthoritativeSecurityTransaction, AuthoritativeSecurityTransaction>();
        services.AddSingleton<ISessionRevocationCleanup, SessionRevocationCleanup>();
        services.AddScoped<IAccountPasswordPolicy, AccountPasswordPolicy>();

        AddSecurityEmailDelivery(services, configuration);

        return services;
    }

    /// <summary>
    /// Security email delivery (ADR-018 section 3): delivery records, the bounded queue, one worker, status-only
    /// reconciliation, and the Notification integration as the only <see cref="ISecurityEmailDispatcher"/>. The host
    /// may replace the dispatcher with the isolated development mail sink (Identity.Api, Debug builds only).
    /// </summary>
    private static void AddSecurityEmailDelivery(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecurityEmailDeliveryOptions>()
            .Bind(configuration.GetSection(SecurityEmailDeliveryOptions.SectionName))
            .Validate(options => options.IsValid(), $"{SecurityEmailDeliveryOptions.SectionName} is out of range.")
            .ValidateOnStart();
        services.AddOptions<NotificationDeliveryOptions>()
            .Bind(configuration.GetSection(NotificationDeliveryOptions.SectionName));

        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<SecurityEmailDeliveryOptions>>().Value;
            var issuer = configuration["Identity:Issuer"] is { Length: > 0 } value
                ? new Uri(value, UriKind.Absolute)
                : throw new InvalidOperationException("Identity:Issuer is required to build security email links.");
            return new SecurityEmailSettings(
                new Uri(issuer, options.ConfirmationPath),
                options.ConfirmationTokenLifetime,
                options.MaxDispatchAttempts,
                options.RetryBaseDelay);
        });

        services.AddSingleton<SecurityEmailQueue>();
        services.AddSingleton<ISecurityEmailQueue>(provider => provider.GetRequiredService<SecurityEmailQueue>());
        services.AddScoped<ISecurityEmailDeliveryStore, SecurityEmailDeliveryStore>();
        services.AddHostedService<SecurityEmailDeliveryWorker>();
        services.AddHostedService<SecurityEmailReconciler>();

        services.AddSingleton<NotificationServiceTokenProvider>();
        services.AddHttpClient(NotificationServiceTokenProvider.HttpClientName);
        services.AddHttpClient<ISecurityEmailDispatcher, NotificationSecurityEmailDispatcher>((provider, client) =>
            client.Timeout = provider.GetRequiredService<IOptions<NotificationDeliveryOptions>>().Value.Timeout);
    }
}
