using System.Text.Json.Serialization;
using EnterpriseCommerce.BuildingBlocks.Authentication;
using EnterpriseCommerce.Inventory.Api.Common;
using EnterpriseCommerce.Inventory.Api.Hosting;
using EnterpriseCommerce.Inventory.Api.Security;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Application.Reservations;
using EnterpriseCommerce.Inventory.Domain.Reservations;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace EnterpriseCommerce.Inventory.Api.Reservations;

/// <summary>One requested line: a variant of the tenant and a whole quantity.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record ReservationLineRequestBody(Guid? VariantId, int? Quantity);

/// <summary><c>POST /internal/v1/inventory/tenants/{tenantId}/reservations</c> body. The tenant comes from the route only.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateReservationRequest(IReadOnlyList<ReservationLineRequestBody?>? Lines, string? Reference, int? ExpiresInSeconds);

/// <summary>One reserved line.</summary>
internal sealed record ReservationLineResponse(Guid InventoryItemId, Guid ProductId, Guid VariantId, int Quantity);

/// <summary>A reservation as its owner sees it (INVENTORY_API_CONTRACT.md section 7).</summary>
internal sealed record ReservationResponse(
    Guid Id,
    Guid TenantId,
    string Status,
    IReadOnlyList<ReservationLineResponse> Lines,
    string? Reference,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? CompletedAt,
    long Version)
{
    public static ReservationResponse Of(Reservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        return new ReservationResponse(
            reservation.Id,
            reservation.TenantId,
            reservation.Status.ToString(),
            [.. reservation.Lines.OrderBy(line => line.InventoryItemId).Select(line => new ReservationLineResponse(line.InventoryItemId, line.ProductId, line.VariantId, line.Quantity))],
            reservation.Reference,
            reservation.CreatedAt,
            reservation.ExpiresAt,
            reservation.CompletedAt,
            reservation.Version);
    }
}

/// <summary>
/// The internal reservation API for checkout orchestration (INVENTORY_API_CONTRACT.md section 7, ADR-026 sections 6 to
/// 10): allow-listed service clients only, each seeing only its own reservations. The Gateway never routes
/// <c>/internal/**</c>.
/// </summary>
internal static class InternalReservationEndpoints
{
    public static IEndpointRouteBuilder MapInternalReservationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reservations = endpoints.MapGroup("/internal/v1/inventory/tenants/{tenantId:guid}/reservations")
            .RequireAuthorization(InventorySecuritySetup.ReservationServicePolicy)
            .WithRequestSizeLimit();
        reservations.MapPost("/", CreateAsync);
        reservations.MapGet("/{reservationId:guid}", GetAsync);
        reservations.MapPost("/{reservationId:guid}/commit", (HttpContext context, Guid tenantId, Guid reservationId, ReservationHandlers handlers, CancellationToken token) =>
            TransitionAsync(context, caller => handlers.CommitAsync(caller, tenantId, reservationId, token)));
        reservations.MapPost("/{reservationId:guid}/release", (HttpContext context, Guid tenantId, Guid reservationId, ReservationHandlers handlers, CancellationToken token) =>
            TransitionAsync(context, caller => handlers.ReleaseAsync(caller, tenantId, reservationId, token)));
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        Guid tenantId,
        CreateReservationRequest request,
        CreateReservationHandler handler,
        CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        if (httpContext.Request.Headers[IdempotencyKey.HeaderName] is not [{ } key] || !IdempotencyKey.IsValid(key))
        {
            return Problems.Validation(IdempotencyKey.Errors());
        }

        // A missing variant or quantity is mapped to an invalid line, which the domain refuses as a whole.
        var lines = request.Lines?.Select(line => new ReservationLineRequest(line?.VariantId ?? Guid.Empty, line?.Quantity ?? 0)).ToList();
        var result = await handler.HandleAsync(new CreateReservationCommand(caller, tenantId, lines, request.Reference, request.ExpiresInSeconds, key), cancellationToken);
        return result.Status switch
        {
            CommandStatus.Created => Results.Created($"/internal/v1/inventory/tenants/{tenantId}/reservations/{result.Value!.Id}", ReservationResponse.Of(result.Value)),
            CommandStatus.Replayed => Results.Ok(ReservationResponse.Of(result.Value!)),
            _ => Problems.For(result, "reservation"),
        };
    }

    private static async Task<IResult> GetAsync(HttpContext httpContext, Guid tenantId, Guid reservationId, ReservationHandlers handlers, CancellationToken cancellationToken)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await handlers.GetAsync(caller, tenantId, reservationId, cancellationToken);
        return result.Status == CommandStatus.Ok ? Results.Ok(ReservationResponse.Of(result.Value!)) : Problems.For(result, "reservation");
    }

    private static async Task<IResult> TransitionAsync(HttpContext httpContext, Func<ServiceCaller, Task<CommandResult<Reservation>>> transition)
    {
        if (CallerOf(httpContext) is not { } caller)
        {
            return Results.Forbid(authenticationSchemes: [JwtBearerDefaults.AuthenticationScheme]);
        }

        var result = await transition(caller);
        return result.Status == CommandStatus.Ok ? Results.Ok(ReservationResponse.Of(result.Value!)) : Problems.For(result, "reservation");
    }

    /// <summary>The validated service client of the request; the policy already requires an allow-listed one.</summary>
    private static ServiceCaller? CallerOf(HttpContext httpContext) =>
        CallerClaims.ServiceClientOf(httpContext.User) is { } client
            ? new ServiceCaller(client, CorrelationIdMiddleware.Get(httpContext) ?? Guid.NewGuid().ToString("D"))
            : null;
}
