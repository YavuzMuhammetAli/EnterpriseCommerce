using EnterpriseCommerce.TenantMerchant.Api.Security;
using EnterpriseCommerce.TenantMerchant.Api.Tenants;
using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EnterpriseCommerce.TenantMerchant.Api.Invitations;

/// <summary>
/// Merchant invitations (TENANT_MERCHANT_API_CONTRACT.md section 5, ADR-021): creation, reads and cancellation by
/// members of the tenant, and acceptance by the invited user. The acceptance token is returned once, in the creation
/// response, and travels back only in the acceptance request body; it is never part of a URL, a read or a log.
/// </summary>
internal static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var invitations = endpoints.MapGroup("/api/v1/tenants/{tenantId:guid}/invitations")
            .RequireAuthorization(TenantMerchantSecuritySetup.MerchantUserPolicy);

        invitations.MapPost("/", CreateAsync);
        invitations.MapGet("/", ListAsync);
        invitations.MapGet("/{invitationId:guid}", GetAsync);
        invitations.MapPost("/{invitationId:guid}/cancel", CancelAsync);

        // Not tenant-scoped: the invitee is not a member yet. The tenant is the invitation's own.
        endpoints.MapPost("/api/v1/merchant-invitations/accept", AcceptAsync)
            .RequireAuthorization(TenantMerchantSecuritySetup.MerchantUserPolicy);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid tenantId,
        CreateInvitationRequest? request,
        HttpContext httpContext,
        CreateInvitationHandler handler,
        CancellationToken cancellationToken)
    {
        if (MembershipEndpoints.CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handler.HandleAsync(new CreateInvitationCommand(tenantId, request?.Email, request?.Role, caller), cancellationToken);
        if (result.Outcome == InvitationCommandOutcome.Created)
        {
            // The token is shown once: no cache may keep this response.
            httpContext.Response.Headers.CacheControl = "no-store";
            httpContext.Response.Headers.Pragma = "no-cache";
            return Results.Created(
                $"/api/v1/tenants/{tenantId}/invitations/{result.Invitation!.Id}",
                new CreatedInvitationResponse(InvitationContractMapping.ToResponse(result.Invitation), result.AcceptanceToken!));
        }

        return Respond(result);
    }

    private static async Task<IResult> ListAsync(
        Guid tenantId,
        string? status,
        int? page,
        int? pageSize,
        HttpContext httpContext,
        InvitationQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerClaims.UserSubjectOf(httpContext.User) is not { } subject)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!PageRequest.TryCreate(page, pageSize, out var pageRequest, out var pageErrors))
        {
            foreach (var (key, value) in pageErrors)
            {
                errors[key] = value;
            }
        }

        InvitationStatus? statusFilter = null;
        if (status is not null)
        {
            if (Enum.GetNames<InvitationStatus>().Contains(status, StringComparer.Ordinal) && Enum.TryParse<InvitationStatus>(status, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors["status"] = [$"The status must be one of: {string.Join(", ", Enum.GetNames<InvitationStatus>())}."];
            }
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var result = await handler.ListAsync(tenantId, statusFilter, subject, pageRequest!, cancellationToken);
        return result.Access switch
        {
            TenantAccess.Allowed => Results.Ok(InvitationContractMapping.ToResponse(result.Page!)),
            TenantAccess.Forbidden => Problems.PermissionDenied(),
            _ => Problems.TenantNotFound(),
        };
    }

    private static async Task<IResult> GetAsync(
        Guid tenantId,
        Guid invitationId,
        HttpContext httpContext,
        InvitationQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerClaims.UserSubjectOf(httpContext.User) is not { } subject)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handler.GetAsync(tenantId, invitationId, subject, cancellationToken);
        return result.Access switch
        {
            TenantAccess.Allowed when result.Invitation is { } invitation => Results.Ok(InvitationContractMapping.ToResponse(invitation)),
            TenantAccess.Allowed => Problems.InvitationNotFound(),
            TenantAccess.Forbidden => Problems.PermissionDenied(),
            _ => Problems.TenantNotFound(),
        };
    }

    private static async Task<IResult> CancelAsync(
        Guid tenantId,
        Guid invitationId,
        HttpContext httpContext,
        CancelInvitationHandler handler,
        CancellationToken cancellationToken)
    {
        if (MembershipEndpoints.CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        return Respond(await handler.HandleAsync(new CancelInvitationCommand(tenantId, invitationId, caller), cancellationToken));
    }

    private static async Task<IResult> AcceptAsync(
        AcceptInvitationRequest? request,
        HttpContext httpContext,
        AcceptInvitationHandler handler,
        CancellationToken cancellationToken)
    {
        if (MembershipEndpoints.CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handler.HandleAsync(new AcceptInvitationCommand(request?.Token, caller), cancellationToken);
        return result.Outcome switch
        {
            AcceptInvitationOutcome.Accepted => Results.Created(
                $"/api/v1/tenants/{result.TenantId}/memberships/{result.Membership!.Id}",
                InvitationContractMapping.ToAccepted(result.TenantId!.Value, result.Membership)),
            AcceptInvitationOutcome.Replayed => Results.Ok(InvitationContractMapping.ToAccepted(result.TenantId!.Value, result.Membership)),
            AcceptInvitationOutcome.Invalid => Problems.Validation(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["token"] = ["The invitation token is required."],
            }),
            AcceptInvitationOutcome.NotPending => Problems.InvitationNotPending(),
            AcceptInvitationOutcome.Expired => Problems.InvitationExpired(),
            AcceptInvitationOutcome.EmailMismatch => Problems.InvitationEmailMismatch(),
            AcceptInvitationOutcome.EmailUnverified => Problems.InvitationEmailUnverified(),
            AcceptInvitationOutcome.AccountIneligible => Problems.InvitationAccountIneligible(),
            AcceptInvitationOutcome.AlreadyMember => Problems.AlreadyAMember(),
            AcceptInvitationOutcome.Conflict => Problems.ConcurrencyConflict(),
            _ => Problems.InvitationNotFound(),
        };
    }

    private static IResult Respond(InvitationCommandResult result) => result.Outcome switch
    {
        InvitationCommandOutcome.Cancelled or InvitationCommandOutcome.Unchanged => Results.Ok(InvitationContractMapping.ToResponse(result.Invitation!)),
        InvitationCommandOutcome.Invalid => Problems.Validation(result.Errors!),
        InvitationCommandOutcome.PermissionDenied => Problems.PermissionDenied(),
        InvitationCommandOutcome.OwnerRoleReserved => Problems.OwnerRoleReserved(),
        InvitationCommandOutcome.AlreadyPending => Problems.InvitationAlreadyPending(result.ExistingInvitationId),
        InvitationCommandOutcome.LimitReached => Problems.InvitationLimitReached(),
        InvitationCommandOutcome.InvitationNotFound => Problems.InvitationNotFound(),
        InvitationCommandOutcome.Expired => Problems.InvitationExpired(),
        InvitationCommandOutcome.InvalidTransition => Problems.InvalidTransition(),
        _ => Problems.TenantNotFound(),
    };
}
