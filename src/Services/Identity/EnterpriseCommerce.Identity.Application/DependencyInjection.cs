using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Application.Sessions;
using EnterpriseCommerce.Identity.Application.Tokens;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Identity.Application;

/// <summary>
/// Registers Application-layer services (use case handlers).
/// Handlers are plain classes resolved from DI; no mediator is used (CLAUDE.md, ADR-009).
/// </summary>
public static class DependencyInjection
{
    /// <param name="services">The service collection.</param>
    /// <param name="reuseSettings">Client facts of the refresh-token reuse policy (ADR-017 section 5).</param>
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services, RefreshReuseSettings reuseSettings)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(reuseSettings);

        services.AddSingleton(reuseSettings);
        services.AddScoped<LogoutAllSessionsHandler>();
        services.AddScoped<EndClientSessionHandler>();
        services.AddScoped<SecurityStateReader>();
        services.AddScoped<RefreshTokenReuseHandler>();
        services.AddScoped<TokenGrantGuard>();
        services.AddScoped<RegisterAccountHandler>();
        services.AddScoped<ResendConfirmationHandler>();
        services.AddScoped<ConfirmEmailHandler>();
        services.AddScoped<RequestPasswordResetHandler>();
        services.AddScoped<ResetPasswordHandler>();
        services.AddScoped<ChangePasswordHandler>();
        services.AddScoped<SecurityEmailScheduler>();
        services.AddScoped<SecurityEmailDeliveryProcessor>();

        return services;
    }
}
