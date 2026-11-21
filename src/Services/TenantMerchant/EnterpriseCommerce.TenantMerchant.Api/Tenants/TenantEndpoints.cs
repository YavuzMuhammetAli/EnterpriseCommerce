using EnterpriseCommerce.TenantMerchant.Api.Security;
using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EnterpriseCommerce.TenantMerchant.Api.Tenants;

/// <summary>
/// The member API (TENANT_MERCHANT_API_CONTRACT.md section 4): user tokens of the merchant portal. The tenant ID in the
/// path is never trusted by itself: every request is authorized against the caller's membership in that tenant.
/// </summary>
internal static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}")
            .RequireAuthorization(TenantMerchantSecuritySetup.MerchantUserPolicy);

        tenant.MapGet("/", GetTenantAsync);
        tenant.MapGet("/memberships", ListMembershipsAsync);
        return endpoints;
    }

    private static async Task<IResult> GetTenantAsync(
        Guid tenantId,
        HttpContext httpContext,
        GetTenantForMemberHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerClaims.UserSubjectOf(httpContext.User) is not { } subject)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handler.HandleAsync(tenantId, subject, cancellationToken);
        return result.Access switch
        {
            TenantAccess.Allowed => Results.Ok(TenantContractMapping.ToMember(result.Tenant!, result.Membership!)),
            TenantAccess.Forbidden => Problems.PermissionDenied(),
            _ => Problems.TenantNotFound(),
        };
    }

    private static async Task<IResult> ListMembershipsAsync(
        Guid tenantId,
        int? page,
        int? pageSize,
        HttpContext httpContext,
        ListMembershipsHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerClaims.UserSubjectOf(httpContext.User) is not { } subject)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        if (!PageRequest.TryCreate(page, pageSize, out var pageRequest, out var errors))
        {
            return Problems.Validation(errors);
        }

        var result = await handler.HandleAsync(tenantId, subject, pageRequest, cancellationToken);
        return result.Access switch
        {
            TenantAccess.Allowed => Results.Ok(TenantContractMapping.ToResponse(result.Page!)),
            TenantAccess.Forbidden => Problems.PermissionDenied(),
            _ => Problems.TenantNotFound(),
        };
    }
}
