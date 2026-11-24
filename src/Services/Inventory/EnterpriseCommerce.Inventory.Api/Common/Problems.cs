using EnterpriseCommerce.Inventory.Application.Common;

namespace EnterpriseCommerce.Inventory.Api.Common;

/// <summary>
/// RFC 9457 problem responses of the Inventory API (INVENTORY_API_CONTRACT.md section 9). The <c>type</c> URIs follow the
/// placeholder scheme of IDENTITY_API_CONTRACT.md section 1.4.
/// </summary>
internal static class Problems
{
    private const string TypeBase = "https://errors.enterprisecommerce.example/inventory/";

    public static IResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors.ToDictionary(), title: "One or more validation errors occurred.", type: TypeBase + "validation-failed");

    /// <summary>Maps a failed command to its problem. <paramref name="resource"/> names the resource of a 404.</summary>
    public static IResult For<T>(CommandResult<T> result, string resource)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            CommandStatus.Invalid => Validation(result.Errors ?? new Dictionary<string, string[]>()),
            CommandStatus.TenantNotVisible => Problem(StatusCodes.Status404NotFound, "tenant-not-found", "The tenant was not found."),
            CommandStatus.PermissionDenied => Problem(StatusCodes.Status403Forbidden, "permission-denied", "Your membership does not allow this operation."),
            CommandStatus.NotFound => Problem(StatusCodes.Status404NotFound, $"{resource}-not-found", $"The {resource.Replace('-', ' ')} was not found."),
            CommandStatus.IdempotencyKeyReused => Problem(
                StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused", "The Idempotency-Key was already used for a different request."),
            CommandStatus.ItemExists => Problem(StatusCodes.Status409Conflict, "inventory-item-exists", "The tenant already tracks this variant."),
            CommandStatus.VariantNotFound => Problem(
                StatusCodes.Status422UnprocessableEntity, "variant-not-found", "The catalog has no such variant in this product of the tenant."),
            CommandStatus.VariantNotEligible => Problem(
                StatusCodes.Status409Conflict, "variant-not-eligible", "The product is archived or not in a state that allows inventory tracking."),
            CommandStatus.InsufficientUnreservedStock => Problem(
                StatusCodes.Status409Conflict, "insufficient-unreserved-stock", "The adjustment would leave on-hand stock below the reserved quantity."),
            CommandStatus.StockLimitExceeded => Problem(StatusCodes.Status409Conflict, "stock-limit-exceeded", "The adjustment would exceed the on-hand maximum."),
            CommandStatus.InventoryItemNotFound => WithVariants(
                StatusCodes.Status422UnprocessableEntity, "inventory-item-not-found", "A line names a variant the tenant does not track.", result.Variants),
            CommandStatus.InsufficientStock => WithVariants(
                StatusCodes.Status409Conflict, "insufficient-stock", "At least one line needs more than the available stock. Nothing was reserved.", result.Variants),
            CommandStatus.ReservationCommitted => Problem(StatusCodes.Status409Conflict, "reservation-committed", "The reservation is committed."),
            CommandStatus.ReservationReleased => Problem(StatusCodes.Status409Conflict, "reservation-released", "The reservation was released."),
            CommandStatus.ReservationExpired => Problem(StatusCodes.Status409Conflict, "reservation-expired", "The reservation expired."),
            CommandStatus.ConcurrencyConflict => Problem(
                StatusCodes.Status409Conflict, "concurrency-conflict", "A concurrent change was made. Read the current state and try again."),
            _ => throw new InvalidOperationException($"No problem is defined for {result.Status}."),
        };
    }

    private static IResult WithVariants(int status, string code, string title, IReadOnlyList<Guid>? variants) =>
        Results.Problem(
            statusCode: status,
            title: title,
            type: TypeBase + code,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["variantIds"] = variants ?? [] });

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, type: TypeBase + code);
}
