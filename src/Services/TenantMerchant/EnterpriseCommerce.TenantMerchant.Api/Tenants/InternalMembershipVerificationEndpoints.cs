using System.Text.Json.Serialization;
using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.TenantMerchant.Api.Security;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EnterpriseCommerce.TenantMerchant.Api.Tenants;

/// <summary>
/// <c>POST /internal/v1/tenants/{tenantId}/membership-verifications</c> body. The subject travels in the body, never in
/// the URL, so it does not reach request logs. Unknown members are rejected with <c>400</c>.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record MembershipVerificationRequest(string? SubjectId);

/// <summary>The verdict: <c>active</c> and the role, or <c>active: false</c> and no role.</summary>
internal sealed record MembershipVerificationResponse(bool Active, string? Role);

/// <summary>
/// Internal membership verification for other services (ADR-025 section 1, TENANT_MERCHANT_API_CONTRACT.md section 3.3).
/// </summary>
/// <remarks>
/// Its own route group with its own policy: mapping it into the provisioning group would require both policies. The
/// Gateway never routes <c>/internal/**</c>.
/// </remarks>
internal static class InternalMembershipVerificationEndpoints
{
    public static IEndpointRouteBuilder MapInternalMembershipVerificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/internal/v1/tenants/{tenantId:guid}/membership-verifications")
            .RequireAuthorization(TenantMerchantSecuritySetup.MembershipVerificationServicePolicy)
            .MapPost("/", VerifyAsync);
        return endpoints;
    }

    private static async Task<IResult> VerifyAsync(
        HttpContext httpContext,
        Guid tenantId,
        MembershipVerificationRequest request,
        VerifyMembershipHandler handler,
        CancellationToken cancellationToken)
    {
        // The policy already requires an allow-listed service client; the endpoint never proceeds without one.
        if (CallerClaims.ServiceClientOf(httpContext.User) is null)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handler.HandleAsync(tenantId, request.SubjectId, cancellationToken);
        if (result.Verdict is not { } verdict)
        {
            return Problems.Validation(result.Errors!);
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new MembershipVerificationResponse(verdict.Active, verdict.Role?.ToString()));
    }
}
