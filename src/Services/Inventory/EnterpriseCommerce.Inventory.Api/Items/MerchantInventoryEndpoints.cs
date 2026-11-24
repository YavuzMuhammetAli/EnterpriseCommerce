using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.Inventory.Api.Common;
using EnterpriseCommerce.Inventory.Api.Hosting;
using EnterpriseCommerce.Inventory.Api.Security;
using EnterpriseCommerce.Inventory.Application.Authorization;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Application.Items;
using EnterpriseCommerce.Inventory.Domain.Items;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Inventory.Api.Items;

/// <summary>
/// The merchant stock management API (INVENTORY_API_CONTRACT.md sections 3 to 6) under
/// <c>/api/v1/inventory/tenants/{tenantId}</c>, for merchant-portal users. Every request is authorized per request by
/// Tenant &amp; Merchant's verdict (ADR-025); every read and write is scoped by the route's tenant.
/// </summary>
internal static class MerchantInventoryEndpoints
{
    public static IEndpointRouteBuilder MapMerchantInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var items = endpoints.MapGroup("/api/v1/inventory/tenants/{tenantId:guid}/items")
            .RequireAuthorization(InventorySecuritySetup.MerchantUserPolicy)
            .WithRequestSizeLimit();
        items.MapPost("/", CreateAsync);
        items.MapGet("/", ListAsync);
        items.MapGet("/{itemId:guid}", GetAsync);
        items.MapPost("/{itemId:guid}/adjustments", AdjustAsync);
        items.MapGet("/{itemId:guid}/movements", ListMovementsAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        Guid tenantId,
        CreateInventoryItemRequest request,
        CreateInventoryItemHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        if (KeyOf(httpContext) is not { } key)
        {
            return Problems.Validation(IdempotencyKey.Errors());
        }

        var result = await handler.HandleAsync(new CreateInventoryItemCommand(caller, tenantId, request.ProductId, request.VariantId, request.InitialOnHand, key), cancellationToken);
        return result.Status switch
        {
            CommandStatus.Created => Results.Created($"/api/v1/inventory/tenants/{tenantId}/items/{result.Value!.Id}", InventoryItemResponse.Of(result.Value)),
            CommandStatus.Replayed => Results.Ok(InventoryItemResponse.Of(result.Value!)),
            _ => Problems.For(result, "inventory-item"),
        };
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        Guid tenantId,
        [FromQuery] Guid? productId,
        [FromQuery] Guid? variantId,
        [FromQuery] string? sku,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        InventoryQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        if (!PageRequest.TryCreate(page, pageSize, out var pageRequest, out var errors))
        {
            return Problems.Validation(errors);
        }

        if (sku is not null && SkuSnapshot.TryCreate(sku) is null)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["sku"] = [$"sku must be 1 to {SkuSnapshot.MaxLength} letters, digits, '.', '_' or '-'."] });
        }

        var result = await handler.ListItemsAsync(caller, tenantId, new InventoryItemFilter(productId, variantId, sku), pageRequest, cancellationToken);
        return result.Status == CommandStatus.Ok
            ? Results.Ok(new PageResponse<InventoryItemResponse>([.. result.Value!.Items.Select(InventoryItemResponse.Of)], result.Value.PageNumber, result.Value.PageSize, result.Value.TotalCount))
            : Problems.For(result, "inventory-item");
    }

    private static async Task<IResult> GetAsync(HttpContext httpContext, Guid tenantId, Guid itemId, InventoryQueryHandler handler, CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handler.GetItemAsync(caller, tenantId, itemId, cancellationToken);
        return result.Status == CommandStatus.Ok ? Results.Ok(InventoryItemResponse.Of(result.Value!)) : Problems.For(result, "inventory-item");
    }

    private static async Task<IResult> AdjustAsync(
        HttpContext httpContext,
        Guid tenantId,
        Guid itemId,
        AdjustStockRequest request,
        AdjustStockHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        if (KeyOf(httpContext) is not { } key)
        {
            return Problems.Validation(IdempotencyKey.Errors());
        }

        var result = await handler.HandleAsync(new AdjustStockCommand(caller, tenantId, itemId, request.QuantityChange, request.Reason, key), cancellationToken);
        return result.Status switch
        {
            CommandStatus.Created => Results.Created(
                $"/api/v1/inventory/tenants/{tenantId}/items/{itemId}/movements",
                new AdjustmentResponse(StockMovementResponse.Of(result.Value!.Movement), InventoryItemResponse.Of(result.Value.Item))),
            CommandStatus.Replayed => Results.Ok(new AdjustmentResponse(StockMovementResponse.Of(result.Value!.Movement), InventoryItemResponse.Of(result.Value.Item))),
            _ => Problems.For(result, "inventory-item"),
        };
    }

    private static async Task<IResult> ListMovementsAsync(
        HttpContext httpContext,
        Guid tenantId,
        Guid itemId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        InventoryQueryHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        if (!PageRequest.TryCreate(page, pageSize, out var pageRequest, out var errors))
        {
            return Problems.Validation(errors);
        }

        var result = await handler.ListMovementsAsync(caller, tenantId, itemId, pageRequest, cancellationToken);
        return result.Status == CommandStatus.Ok
            ? Results.Ok(new PageResponse<StockMovementResponse>([.. result.Value!.Items.Select(StockMovementResponse.Of)], result.Value.PageNumber, result.Value.PageSize, result.Value.TotalCount))
            : Problems.For(result, "inventory-item");
    }

    /// <summary>The user subject of the validated token and the request's correlation ID; <c>null</c> for anything else.</summary>
    private static MerchantCaller? CallerOf(HttpContext httpContext) =>
        CallerClaims.UserSubjectOf(httpContext.User) is { } subject && subject.Length <= Domain.Common.Actor.IdMaxLength
            ? new MerchantCaller(subject, CorrelationIdMiddleware.Get(httpContext) ?? Guid.NewGuid().ToString("D"))
            : null;

    private static string? KeyOf(HttpContext httpContext) =>
        httpContext.Request.Headers[IdempotencyKey.HeaderName] is [{ } value] && IdempotencyKey.IsValid(value) ? value : null;
}
