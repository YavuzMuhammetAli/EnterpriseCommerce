using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.TenantMerchant.Api.Hosting;
using EnterpriseCommerce.TenantMerchant.Api.Security;
using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EnterpriseCommerce.TenantMerchant.Api.Tenants;

/// <summary>
/// Membership administration and the caller's tenants (TENANT_MERCHANT_API_CONTRACT.md sections 4.3 to 4.6, ADR-021).
/// The caller is always the token's <c>sub</c>; the tenant and membership IDs in the path are authorized against
/// this service's own membership data, in the tenant's serialized write transaction for every change.
/// </summary>
internal static class MembershipEndpoints
{
    public static IEndpointRouteBuilder MapMembershipEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/tenants", ListCallerTenantsAsync)
            .RequireAuthorization(TenantMerchantSecuritySetup.MerchantUserPolicy);

        var membership = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/memberships/{membershipId:guid}")
            .RequireAuthorization(TenantMerchantSecuritySetup.MerchantUserPolicy);

        membership.MapGet("/", GetMembershipAsync);
        membership.MapPost("/suspend", (Guid tenantId, Guid membershipId, HttpContext httpContext, MembershipAdministrationHandler handler, CancellationToken ct) =>
            ChangeStatusAsync(tenantId, membershipId, MembershipAdministrationAction.Suspend, httpContext, handler, ct));
        membership.MapPost("/reactivate", (Guid tenantId, Guid membershipId, HttpContext httpContext, MembershipAdministrationHandler handler, CancellationToken ct) =>
            ChangeStatusAsync(tenantId, membershipId, MembershipAdministrationAction.Reactivate, httpContext, handler, ct));
        membership.MapPost("/remove", (Guid tenantId, Guid membershipId, HttpContext httpContext, MembershipAdministrationHandler handler, CancellationToken ct) =>
            ChangeStatusAsync(tenantId, membershipId, MembershipAdministrationAction.Remove, httpContext, handler, ct));
        membership.MapPut("/role", ChangeRoleAsync);
        return endpoints;
    }

    private static async Task<IResult> ListCallerTenantsAsync(
        int? page,
        int? pageSize,
        HttpContext httpContext,
        ListCallerTenantsHandler handler,
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

        return Results.Ok(TenantContractMapping.ToResponse(await handler.HandleAsync(subject, pageRequest, cancellationToken)));
    }

    private static async Task<IResult> GetMembershipAsync(
        Guid tenantId,
        Guid membershipId,
        HttpContext httpContext,
        GetMembershipHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerClaims.UserSubjectOf(httpContext.User) is not { } subject)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handler.HandleAsync(tenantId, membershipId, subject, cancellationToken);
        return result.Access switch
        {
            TenantAccess.Allowed when result.Membership is { } membership => Results.Ok(TenantContractMapping.ToResponse(membership)),
            TenantAccess.Allowed => Problems.MembershipNotFound(),
            TenantAccess.Forbidden => Problems.PermissionDenied(),
            _ => Problems.TenantNotFound(),
        };
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid tenantId,
        Guid membershipId,
        MembershipAdministrationAction action,
        HttpContext httpContext,
        MembershipAdministrationHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        return Respond(await handler.HandleAsync(new ChangeMembershipStatusCommand(tenantId, membershipId, action, caller), cancellationToken));
    }

    private static async Task<IResult> ChangeRoleAsync(
        Guid tenantId,
        Guid membershipId,
        ChangeRoleRequest? request,
        HttpContext httpContext,
        MembershipAdministrationHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        return Respond(await handler.HandleAsync(new ChangeMembershipRoleCommand(tenantId, membershipId, request?.Role, caller), cancellationToken));
    }

    internal static MemberCaller? CallerOf(HttpContext httpContext) =>
        CallerClaims.UserSubjectOf(httpContext.User) is { } subject ? new MemberCaller(subject, CorrelationIdMiddleware.Get(httpContext)) : null;

    private static IResult Respond(MembershipCommandResult result) => result.Outcome switch
    {
        MembershipCommandOutcome.Changed or MembershipCommandOutcome.Unchanged => Results.Ok(TenantContractMapping.ToResponse(result.Membership!)),
        MembershipCommandOutcome.Invalid => Problems.Validation(result.Errors!),
        MembershipCommandOutcome.PermissionDenied => Problems.PermissionDenied(),
        MembershipCommandOutcome.SelfModification => Problems.SelfModificationForbidden(),
        MembershipCommandOutcome.MembershipNotFound => Problems.MembershipNotFound(),
        MembershipCommandOutcome.InvalidTransition => Problems.InvalidTransition(),
        MembershipCommandOutcome.LastActiveOwner => Problems.LastActiveOwner(),
        MembershipCommandOutcome.OwnerRoleReserved => Problems.OwnerRoleReserved(),
        MembershipCommandOutcome.Conflict => Problems.ConcurrencyConflict(),
        _ => Problems.TenantNotFound(),
    };
}
