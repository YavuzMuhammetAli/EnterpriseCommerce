using System.Security.Claims;
using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.TenantMerchant.Api.Hosting;
using EnterpriseCommerce.TenantMerchant.Api.Security;
using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Onboarding;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.TenantMerchant.Api.Tenants;

/// <summary>
/// The member API (TENANT_MERCHANT_API_CONTRACT.md section 4): user tokens of the merchant portal. The tenant ID in the
/// path is never trusted by itself: every request is authorized against the caller's membership in that tenant.
/// Self-service onboarding (section 4.7) creates a tenant whose owner is always the caller.
/// </summary>
internal static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenant = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}")
            .RequireAuthorization(TenantMerchantSecuritySetup.MerchantUserPolicy);

        tenant.MapGet("/", GetTenantAsync);
        tenant.MapGet("/memberships", ListMembershipsAsync);

        endpoints.MapPost("/api/v1/tenants", CreateAsync)
            .RequireAuthorization(TenantMerchantSecuritySetup.MerchantUserPolicy);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        [FromHeader(Name = IdempotencyKey.HeaderName)] string? idempotencyKey,
        CreateTenantRequest? request,
        CreateTenantHandler handler,
        CancellationToken cancellationToken)
    {
        // The owner is the token's subject. The client is recorded for audit; access tokens always carry it (RFC 9068
        // section 2.2), and a token without one is refused rather than recorded without its origin.
        if (CallerClaims.UserSubjectOf(httpContext.User) is not { } subject
            || httpContext.User.FindFirstValue(CallerClaims.ClientId) is not { Length: > 0 } clientId)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        if (!IdempotencyKey.IsValid(idempotencyKey))
        {
            return Problems.Validation(IdempotencyKey.Errors());
        }

        var result = await handler.HandleAsync(
            new CreateTenantCommand(subject, clientId, idempotencyKey!, request?.Slug, request?.DisplayName, CorrelationIdMiddleware.Get(httpContext)),
            cancellationToken);

        return result.Outcome switch
        {
            CreateTenantOutcome.Created => Results.Created(
                $"/api/v1/tenants/{result.Tenant!.Id}",
                TenantContractMapping.ToMember(result.Tenant, result.Membership!)),
            CreateTenantOutcome.Replayed => Results.Ok(TenantContractMapping.ToMember(result.Tenant!, result.Membership!)),
            CreateTenantOutcome.Invalid => Problems.Validation(result.Errors!),
            CreateTenantOutcome.SlugTaken => Problems.SlugTaken(),
            CreateTenantOutcome.IdempotencyKeyReused => Problems.IdempotencyKeyReused(),
            CreateTenantOutcome.TenantLimitReached => Problems.TenantLimitReached(),
            CreateTenantOutcome.NotAccessible => Problems.TenantNotFound(),
            _ => throw new InvalidOperationException($"Unexpected onboarding outcome {result.Outcome}."),
        };
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
