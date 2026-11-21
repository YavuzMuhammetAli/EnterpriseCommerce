using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Application.Memberships;
using EnterpriseCommerce.TenantMerchant.Application.Onboarding;
using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.TenantMerchant.Application;

/// <summary>Registers the Application-layer use cases.</summary>
public static class DependencyInjection
{
    /// <summary>Handlers are plain classes resolved from DI; no mediator is used (CLAUDE.md, ADR-009).</summary>
    public static IServiceCollection AddTenantMerchantApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<TenantAccessAuthorizer>();
        services.AddScoped<ProvisionTenantHandler>();
        services.AddScoped<GetTenantForMemberHandler>();
        services.AddScoped<ListMembershipsHandler>();
        services.AddScoped<GetTenantForServiceHandler>();
        services.AddScoped<CreateInvitationHandler>();
        services.AddScoped<CancelInvitationHandler>();
        services.AddScoped<InvitationQueryHandler>();
        services.AddScoped<AcceptInvitationHandler>();
        services.AddScoped<MembershipAdministrationHandler>();
        services.AddScoped<GetMembershipHandler>();
        services.AddScoped<ListCallerTenantsHandler>();
        services.AddScoped<CreateTenantHandler>();
        return services;
    }
}
