using System.Text.Json.Serialization;
using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.Catalog.Api.Common;
using EnterpriseCommerce.Catalog.Api.Hosting;
using EnterpriseCommerce.Catalog.Api.Security;
using EnterpriseCommerce.Catalog.Application.Products;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EnterpriseCommerce.Catalog.Api.Products;

/// <summary><c>POST /internal/v1/catalog/tenants/{tenantId}/variant-verifications</c> body. Unknown members answer <c>400</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record VariantVerificationRequest(Guid? ProductId, Guid? VariantId);

/// <summary>The verdict: nothing but whether the variant exists in the tenant's product, the product status, sellability and SKU.</summary>
internal sealed record VariantVerificationResponse(bool Found, string? ProductStatus, bool? VariantActive, string? Sku);

/// <summary>
/// The internal variant verification for allow-listed service clients (ADR-027 section 1, CATALOG_API_CONTRACT.md
/// section 6a). The Gateway never routes <c>/internal/**</c>.
/// </summary>
internal static class VariantVerificationEndpoints
{
    public static IEndpointRouteBuilder MapVariantVerificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGroup("/internal/v1/catalog/tenants/{tenantId:guid}/variant-verifications")
            .RequireAuthorization(CatalogSecuritySetup.VariantVerificationServicePolicy)
            .WithRequestSizeLimit()
            .MapPost("/", VerifyAsync);
        return endpoints;
    }

    private static async Task<IResult> VerifyAsync(
        HttpContext httpContext,
        Guid tenantId,
        VariantVerificationRequest request,
        VariantVerificationHandler handler,
        CancellationToken cancellationToken)
    {
        // The policy already requires an allow-listed service client; the endpoint never proceeds without one.
        if (CallerClaims.ServiceClientOf(httpContext.User) is null)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.ProductId is not { } productId || productId == Guid.Empty)
        {
            errors["productId"] = ["A product ID is required."];
        }

        if (request.VariantId is not { } variantId || variantId == Guid.Empty)
        {
            errors["variantId"] = ["A variant ID is required."];
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var verdict = await handler.VerifyAsync(tenantId, request.ProductId!.Value, request.VariantId!.Value, cancellationToken);
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new VariantVerificationResponse(verdict.Found, verdict.ProductStatus, verdict.VariantActive, verdict.Sku));
    }
}
