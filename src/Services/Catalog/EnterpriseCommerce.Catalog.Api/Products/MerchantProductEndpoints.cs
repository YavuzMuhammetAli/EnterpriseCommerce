using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.Catalog.Api.Common;
using EnterpriseCommerce.Catalog.Api.Hosting;
using EnterpriseCommerce.Catalog.Api.Security;
using EnterpriseCommerce.Catalog.Application.Authorization;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Application.Products;
using EnterpriseCommerce.Catalog.Domain.Products;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Catalog.Api.Products;

/// <summary>
/// Merchant product management (CATALOG_API_CONTRACT.md section 4): merchant-portal users only, authorized per request
/// by Tenant &amp; Merchant's verdict for the route tenant (ADR-025). Every read and write is scoped to that tenant.
/// </summary>
internal static class MerchantProductEndpoints
{
    public static IEndpointRouteBuilder MapMerchantProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var products = endpoints.MapGroup("/api/v1/catalog/tenants/{tenantId:guid}/products")
            .RequireAuthorization(CatalogSecuritySetup.MerchantUserPolicy)
            .WithRequestSizeLimit();

        products.MapPost("/", CreateAsync);
        products.MapGet("/", ListAsync);
        products.MapGet("/{productId:guid}", GetAsync);
        products.MapPut("/{productId:guid}", UpdateAsync);
        products.MapPut("/{productId:guid}/media", ReplaceMediaAsync);
        products.MapPost("/{productId:guid}/publish", (HttpContext context, Guid tenantId, Guid productId, ProductLifecycleHandler handler, CancellationToken token) =>
            CommandAsync(context, tenantId, productId, ProductCommand.Publish, handler, token));
        products.MapPost("/{productId:guid}/unpublish", (HttpContext context, Guid tenantId, Guid productId, ProductLifecycleHandler handler, CancellationToken token) =>
            CommandAsync(context, tenantId, productId, ProductCommand.Unpublish, handler, token));
        products.MapPost("/{productId:guid}/archive", (HttpContext context, Guid tenantId, Guid productId, ProductLifecycleHandler handler, CancellationToken token) =>
            CommandAsync(context, tenantId, productId, ProductCommand.Archive, handler, token));
        products.MapPost("/{productId:guid}/restore", (HttpContext context, Guid tenantId, Guid productId, ProductLifecycleHandler handler, CancellationToken token) =>
            CommandAsync(context, tenantId, productId, ProductCommand.Restore, handler, token));
        return endpoints;
    }

    /// <summary>The caller of a merchant request; <c>null</c> for anything but a merchant-portal user token.</summary>
    public static MerchantCaller? CallerOf(HttpContext httpContext) =>
        CallerClaims.UserSubjectOf(httpContext.User) is { } subject ? new MerchantCaller(subject, CorrelationIdMiddleware.Get(httpContext)) : null;

    public static IResult Forbid() => Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        Guid tenantId,
        [FromHeader(Name = IdempotencyKey.HeaderName)] string? idempotencyKey,
        ProductRequest request,
        CreateProductHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Forbid();
        }

        if (!IdempotencyKey.IsValid(idempotencyKey))
        {
            return Problems.Validation(IdempotencyKey.Errors());
        }

        var result = await handler.HandleAsync(caller, tenantId, idempotencyKey!, request.ToInput(), cancellationToken);
        return result.Status switch
        {
            CommandStatus.Created => Created(httpContext, result.Value!),
            CommandStatus.Replayed => Ok(httpContext, result.Value!),
            _ => Problems.For(result.Status, "product", result.Errors, result.Unmet),
        };
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        Guid tenantId,
        [FromQuery] string? status,
        [FromQuery] string? q,
        [FromQuery] string? sku,
        [FromQuery] string? sort,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        MerchantProductQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Forbid();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!PageRequest.TryCreate(page, pageSize, out var pageRequest, out var pageErrors))
        {
            foreach (var (field, messages) in pageErrors)
            {
                errors[field] = messages;
            }
        }

        if (!QueryParameters.TryParseExact<ProductStatus>(status, out var statusFilter))
        {
            QueryParameters.Add(errors, "status", "status must be Draft, Published or Archived.");
        }

        if (!QueryParameters.TryParseSort(sort, out var sortOrder))
        {
            QueryParameters.Add(errors, "sort", "sort must be newest or oldest.");
        }

        if (!SearchText.TryCreate(q, out var text))
        {
            QueryParameters.Add(errors, "q", $"q must be {SearchText.Rule}");
        }

        Sku? skuFilter = null;
        if (!string.IsNullOrEmpty(sku) && !Sku.TryCreate(sku, out skuFilter))
        {
            QueryParameters.Add(errors, "sku", $"sku must be {Sku.Rule}");
        }

        if (errors.Count > 0)
        {
            return Problems.Validation(errors);
        }

        var result = await handler.ListAsync(
            caller, tenantId, new MerchantProductFilter(statusFilter, text, skuFilter?.Key, sortOrder), pageRequest!, cancellationToken);
        return result.Value is { } products
            ? Results.Ok(new PageResponse<MerchantProductResponse>(
                [.. products.Items.Select(MerchantProductResponse.Of)], products.PageNumber, products.PageSize, products.TotalCount))
            : Problems.For(result.Status, "product");
    }

    private static async Task<IResult> GetAsync(
        HttpContext httpContext,
        Guid tenantId,
        Guid productId,
        MerchantProductQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Forbid();
        }

        var result = await handler.GetAsync(caller, tenantId, productId, cancellationToken);
        return result.Value is { } product ? Ok(httpContext, product) : Problems.For(result.Status, "product");
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext httpContext,
        Guid tenantId,
        Guid productId,
        ProductRequest request,
        UpdateProductHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Forbid();
        }

        if (EntityTags.ExpectedVersion(httpContext.Request) is not { } version)
        {
            return Problems.PreconditionRequired();
        }

        var result = await handler.HandleAsync(caller, tenantId, productId, version, request.ToInput(), cancellationToken);
        return result.Value is { } product ? Ok(httpContext, product) : Problems.For(result.Status, "product", result.Errors, result.Unmet);
    }

    private static async Task<IResult> ReplaceMediaAsync(
        HttpContext httpContext,
        Guid tenantId,
        Guid productId,
        MediaListRequest request,
        ReplaceProductMediaHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Forbid();
        }

        if (EntityTags.ExpectedVersion(httpContext.Request) is not { } version)
        {
            return Problems.PreconditionRequired();
        }

        var result = await handler.HandleAsync(caller, tenantId, productId, version, request.ToInput(), cancellationToken);
        return result.Value is { } product ? Ok(httpContext, product) : Problems.For(result.Status, "product", result.Errors);
    }

    private static async Task<IResult> CommandAsync(
        HttpContext httpContext,
        Guid tenantId,
        Guid productId,
        ProductCommand command,
        ProductLifecycleHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Forbid();
        }

        var result = await handler.HandleAsync(caller, tenantId, productId, command, cancellationToken);
        return result.Value is { } product ? Ok(httpContext, product) : Problems.For(result.Status, "product", result.Errors, result.Unmet);
    }

    private static IResult Ok(HttpContext httpContext, Product product)
    {
        EntityTags.Set(httpContext, product.Version);
        return Results.Ok(MerchantProductResponse.Of(product));
    }

    private static IResult Created(HttpContext httpContext, Product product)
    {
        EntityTags.Set(httpContext, product.Version);
        return Results.Created($"/api/v1/catalog/tenants/{product.TenantId}/products/{product.Id}", MerchantProductResponse.Of(product));
    }
}
