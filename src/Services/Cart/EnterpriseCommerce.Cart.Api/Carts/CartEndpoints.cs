using EnterpriseCommerce.Cart.Api.Common;
using EnterpriseCommerce.Cart.Api.Hosting;
using EnterpriseCommerce.Cart.Api.Security;
using EnterpriseCommerce.Cart.Application.Carts;
using EnterpriseCommerce.Cart.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace EnterpriseCommerce.Cart.Api.Carts;

/// <summary>
/// The customer's cart (CART_API_CONTRACT.md, ADR-029). Every endpoint requires a storefront customer, and the cart is
/// always the token subject's own: no route, query or body value names a cart or an owner.
/// </summary>
internal static class CartEndpoints
{
    public const string ItemsPath = "/api/v1/cart/items";

    public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var cart = endpoints.MapGroup("/api/v1/cart")
            .RequireAuthorization(CartSecuritySetup.CustomerPolicy)
            .WithRequestSizeLimit()
            .AddEndpointFilter(async (context, next) =>
            {
                // A cart is personal data: no shared cache may keep any answer, a problem included.
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                return await next(context);
            });

        cart.MapGet("/", GetAsync);
        cart.MapPost("/items", AddItemAsync);
        cart.MapPut("/items/{itemId:guid}", SetQuantityAsync);
        cart.MapDelete("/items/{itemId:guid}", RemoveItemAsync);
        cart.MapDelete("/items", ClearAsync);
        cart.MapPost("/refresh", RefreshAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(HttpContext httpContext, CartReader reader)
    {
        var view = await reader.GetAsync(Subject(httpContext), httpContext.RequestAborted);
        return Ok(httpContext, view);
    }

    private static async Task<IResult> AddItemAsync(HttpContext httpContext, [FromBody] AddItemRequest request, CartCommands commands)
    {
        var result = await commands.AddItemAsync(
            Subject(httpContext), request.ProductId, request.VariantId, request.Quantity, EntityTags.ExpectedVersion(httpContext.Request),
            CorrelationIdMiddleware.Get(httpContext), httpContext.RequestAborted);
        if (!result.Succeeded)
        {
            return Problems.For(result);
        }

        if (result.Status == CartStatus.Created)
        {
            var body = Body(httpContext, result.Cart);
            return Results.Created($"{ItemsPath}/{result.ItemId:D}", body);
        }

        return Ok(httpContext, result.Cart);
    }

    private static async Task<IResult> SetQuantityAsync(HttpContext httpContext, Guid itemId, [FromBody] SetQuantityRequest request, CartCommands commands)
    {
        var result = await commands.SetQuantityAsync(Subject(httpContext), itemId, request.Quantity, EntityTags.ExpectedVersion(httpContext.Request), httpContext.RequestAborted);
        return result.Succeeded ? Ok(httpContext, result.Cart) : Problems.For(result);
    }

    private static async Task<IResult> RemoveItemAsync(HttpContext httpContext, Guid itemId, CartCommands commands)
    {
        var result = await commands.RemoveItemAsync(Subject(httpContext), itemId, EntityTags.ExpectedVersion(httpContext.Request), httpContext.RequestAborted);
        return result.Succeeded ? Ok(httpContext, result.Cart) : Problems.For(result);
    }

    private static async Task<IResult> ClearAsync(HttpContext httpContext, CartCommands commands)
    {
        var result = await commands.ClearAsync(Subject(httpContext), EntityTags.ExpectedVersion(httpContext.Request), httpContext.RequestAborted);
        return result.Succeeded ? Ok(httpContext, result.Cart) : Problems.For(result);
    }

    private static async Task<IResult> RefreshAsync(HttpContext httpContext, CartCommands commands)
    {
        var result = await commands.RefreshAsync(
            Subject(httpContext), EntityTags.ExpectedVersion(httpContext.Request), CorrelationIdMiddleware.Get(httpContext), httpContext.RequestAborted);
        if (!result.Succeeded)
        {
            return Problems.For(result);
        }

        var cart = Body(httpContext, result.Cart);
        return Results.Ok(new RefreshResponse(cart, [.. (result.Changes ?? []).Select(ChangeResponse.From)]));
    }

    private static IResult Ok(HttpContext httpContext, CartView? view) => Results.Ok(Body(httpContext, view));

    /// <summary>The response body, with the cart's entity tag on the response.</summary>
    private static CartResponse Body(HttpContext httpContext, CartView? view)
    {
        var services = httpContext.RequestServices;
        var body = CartResponse.From(
            view,
            services.GetRequiredService<TimeProvider>().GetUtcNow(),
            services.GetRequiredService<CartPricingSettings>().StaleAfter,
            services.GetRequiredService<CartLifecycleSettings>().InactiveCartRetention);
        httpContext.Response.Headers.ETag = EntityTags.For(body.Version);
        return body;
    }

    /// <summary>The caller's subject. The customer policy has already required it, so it is never missing here.</summary>
    private static string Subject(HttpContext httpContext) =>
        CartSecuritySetup.CustomerSubjectOf(httpContext.User, httpContext.RequestServices.GetRequiredService<CustomerAudiences>().Values)
        ?? throw new InvalidOperationException("The customer policy admitted a caller without a customer subject.");
}
