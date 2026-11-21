using EnterpriseCommerce.TenantMerchant.Api.Hosting;
using EnterpriseCommerce.TenantMerchant.Api.Security;
using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.TenantMerchant.Api.Tenants;

/// <summary>
/// Internal tenant provisioning (TENANT_MERCHANT_API_CONTRACT.md section 3): service tokens of allow-listed
/// <c>svc-</c> clients only. The Gateway never routes <c>/internal/**</c>.
/// </summary>
internal static class InternalTenantEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const int IdempotencyKeyMaxLength = 100;

    public static IEndpointRouteBuilder MapInternalTenantEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var tenants = endpoints.MapGroup("/internal/v1/tenants")
            .RequireAuthorization(TenantMerchantSecuritySetup.ProvisioningServicePolicy);

        tenants.MapPost("/", ProvisionAsync);
        tenants.MapGet("/{tenantId:guid}", GetAsync);
        return endpoints;
    }

    private static async Task<IResult> ProvisionAsync(
        HttpContext httpContext,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        ProvisionTenantRequest request,
        ProvisionTenantHandler handler,
        CancellationToken cancellationToken)
    {
        // The policy already requires an allow-listed service client; the endpoint never proceeds without one.
        if (CallerClaims.ServiceClientOf(httpContext.User) is not { } clientId)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        if (!IsValidIdempotencyKey(idempotencyKey))
        {
            return Problems.Validation(new Dictionary<string, string[]>
            {
                [IdempotencyKeyHeader] = [$"The {IdempotencyKeyHeader} header is required: 1 to {IdempotencyKeyMaxLength} characters of letters, digits, '.', '_', ':' or '-' (a UUID is recommended)."],
            });
        }

        var result = await handler.HandleAsync(
            new ProvisionTenantCommand(
                clientId, idempotencyKey!, request.Slug, request.DisplayName, request.OwnerSubjectId, request.ActorReference, CorrelationIdMiddleware.Get(httpContext)),
            cancellationToken);

        return result.Outcome switch
        {
            ProvisionTenantOutcome.Created => Results.Created(
                $"/internal/v1/tenants/{result.Tenant!.Id}",
                TenantContractMapping.ToProvisioned(result.Tenant, result.Owner)),
            ProvisionTenantOutcome.Replayed => Results.Ok(TenantContractMapping.ToProvisioned(result.Tenant!, result.Owner)),
            ProvisionTenantOutcome.Invalid => Problems.Validation(result.Errors!),
            ProvisionTenantOutcome.SlugTaken => Problems.SlugTaken(),
            ProvisionTenantOutcome.ProvisioningKeyReused => Problems.IdempotencyKeyReused(),
            _ => throw new InvalidOperationException($"Unexpected provisioning outcome {result.Outcome}."),
        };
    }

    private static async Task<IResult> GetAsync(Guid tenantId, GetTenantForServiceHandler handler, CancellationToken cancellationToken) =>
        await handler.HandleAsync(tenantId, cancellationToken) is { } result
            ? Results.Ok(TenantContractMapping.ToProvisioned(result.Tenant, result.InitialOwner))
            : Problems.TenantNotFound();

    private static bool IsValidIdempotencyKey(string? value) =>
        value is { Length: > 0 and <= IdempotencyKeyMaxLength }
        && value.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '.' or '_' or ':' or '-');
}
