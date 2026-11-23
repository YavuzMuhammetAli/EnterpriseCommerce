using System.Text.Json.Serialization;
using EnterpriseCommerce.Catalog.Api.Common;
using EnterpriseCommerce.Catalog.Api.Hosting;
using EnterpriseCommerce.Catalog.Api.Products;
using EnterpriseCommerce.Catalog.Api.Security;
using EnterpriseCommerce.Catalog.Application.Brands;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Brands;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Catalog.Api.Brands;

/// <summary><c>POST</c> and <c>PUT</c> body of a brand. Unknown members (a tenant, a status) answer <c>400</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record BrandRequest(string? Slug, string? Name, string? Description);

internal sealed record BrandResponse(Guid Id, Guid TenantId, string Slug, string Name, string? Description, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version)
{
    public static BrandResponse Of(Brand brand)
    {
        ArgumentNullException.ThrowIfNull(brand);
        return new BrandResponse(brand.Id, brand.TenantId, brand.Slug.Value, brand.Name, brand.Description, brand.Status.ToString(), brand.CreatedAt, brand.UpdatedAt, brand.Version);
    }
}

/// <summary>
/// Tenant brands (CATALOG_API_CONTRACT.md section 5): merchant-portal users only, authorized per request by Tenant
/// &amp; Merchant's verdict for the route tenant (ADR-025). Changes need <c>MerchantOwner</c> or <c>MerchantAdmin</c>.
/// </summary>
internal static class BrandEndpoints
{
    public static IEndpointRouteBuilder MapBrandEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var brands = endpoints.MapGroup("/api/v1/catalog/tenants/{tenantId:guid}/brands")
            .RequireAuthorization(CatalogSecuritySetup.MerchantUserPolicy)
            .WithRequestSizeLimit();

        brands.MapPost("/", CreateAsync);
        brands.MapGet("/", ListAsync);
        brands.MapGet("/{brandId:guid}", GetAsync);
        brands.MapPut("/{brandId:guid}", UpdateAsync);
        brands.MapPost("/{brandId:guid}/retire", (HttpContext context, Guid tenantId, Guid brandId, BrandHandlers handler, CancellationToken token) =>
            ChangeStateAsync(context, tenantId, brandId, BrandCommand.Retire, handler, token));
        brands.MapPost("/{brandId:guid}/reactivate", (HttpContext context, Guid tenantId, Guid brandId, BrandHandlers handler, CancellationToken token) =>
            ChangeStateAsync(context, tenantId, brandId, BrandCommand.Reactivate, handler, token));
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(HttpContext httpContext, Guid tenantId, BrandRequest request, BrandHandlers handler, CancellationToken cancellationToken)
    {
        if (MerchantProductEndpoints.CallerOf(httpContext) is not { } caller)
        {
            return MerchantProductEndpoints.Forbid();
        }

        var result = await handler.CreateAsync(caller, tenantId, new BrandInput(request.Slug, request.Name, request.Description), cancellationToken);
        if (result.Status == CommandStatus.Created)
        {
            EntityTags.Set(httpContext, result.Value!.Version);
            return Results.Created($"/api/v1/catalog/tenants/{tenantId}/brands/{result.Value.Id}", BrandResponse.Of(result.Value));
        }

        return Problems.For(result.Status, "brand", result.Errors);
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        Guid tenantId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        BrandHandlers handler,
        CancellationToken cancellationToken)
    {
        if (MerchantProductEndpoints.CallerOf(httpContext) is not { } caller)
        {
            return MerchantProductEndpoints.Forbid();
        }

        if (!PageRequest.TryCreate(page, pageSize, out var pageRequest, out var errors))
        {
            return Problems.Validation(errors);
        }

        var result = await handler.ListAsync(caller, tenantId, pageRequest, cancellationToken);
        return result.Value is { } brands
            ? Results.Ok(new PageResponse<BrandResponse>([.. brands.Items.Select(BrandResponse.Of)], brands.PageNumber, brands.PageSize, brands.TotalCount))
            : Problems.For(result.Status, "brand");
    }

    private static async Task<IResult> GetAsync(HttpContext httpContext, Guid tenantId, Guid brandId, BrandHandlers handler, CancellationToken cancellationToken)
    {
        if (MerchantProductEndpoints.CallerOf(httpContext) is not { } caller)
        {
            return MerchantProductEndpoints.Forbid();
        }

        return Respond(httpContext, await handler.GetAsync(caller, tenantId, brandId, cancellationToken));
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext httpContext,
        Guid tenantId,
        Guid brandId,
        BrandRequest request,
        BrandHandlers handler,
        CancellationToken cancellationToken)
    {
        if (MerchantProductEndpoints.CallerOf(httpContext) is not { } caller)
        {
            return MerchantProductEndpoints.Forbid();
        }

        if (EntityTags.ExpectedVersion(httpContext.Request) is not { } version)
        {
            return Problems.PreconditionRequired();
        }

        return Respond(httpContext, await handler.UpdateAsync(
            caller, tenantId, brandId, version, new BrandInput(request.Slug, request.Name, request.Description), cancellationToken));
    }

    private static async Task<IResult> ChangeStateAsync(
        HttpContext httpContext,
        Guid tenantId,
        Guid brandId,
        BrandCommand command,
        BrandHandlers handler,
        CancellationToken cancellationToken)
    {
        if (MerchantProductEndpoints.CallerOf(httpContext) is not { } caller)
        {
            return MerchantProductEndpoints.Forbid();
        }

        return Respond(httpContext, await handler.ChangeStateAsync(caller, tenantId, brandId, command, cancellationToken));
    }

    private static IResult Respond(HttpContext httpContext, CommandResult<Brand> result)
    {
        if (result.Value is not { } brand)
        {
            return Problems.For(result.Status, "brand", result.Errors);
        }

        EntityTags.Set(httpContext, brand.Version);
        return Results.Ok(BrandResponse.Of(brand));
    }
}
