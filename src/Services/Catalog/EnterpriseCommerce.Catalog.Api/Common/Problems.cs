using EnterpriseCommerce.Catalog.Application.Common;

namespace EnterpriseCommerce.Catalog.Api.Common;

/// <summary>
/// RFC 9457 problem responses of the Catalog API (CATALOG_API_CONTRACT.md section 9). The <c>type</c> URIs follow the
/// placeholder scheme of IDENTITY_API_CONTRACT.md section 1.4.
/// </summary>
internal static class Problems
{
    private const string TypeBase = "https://errors.enterprisecommerce.example/catalog/";

    public static IResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors.ToDictionary(), title: "One or more validation errors occurred.", type: TypeBase + "validation-failed");

    public static IResult PreconditionRequired() =>
        Problem(StatusCodes.Status428PreconditionRequired, "precondition-required", "This change requires an If-Match header with the entity tag of the current version.");

    /// <summary>Maps a failed command status to its problem. <paramref name="resource"/> names the resource of a 404.</summary>
    public static IResult For(CommandStatus status, string resource, IReadOnlyDictionary<string, string[]>? errors = null, IReadOnlyList<string>? unmet = null) =>
        status switch
        {
            CommandStatus.Invalid => Validation(errors ?? new Dictionary<string, string[]>()),
            CommandStatus.TenantNotVisible => Problem(StatusCodes.Status404NotFound, "tenant-not-found", "The tenant was not found."),
            CommandStatus.PermissionDenied => Problem(StatusCodes.Status403Forbidden, "permission-denied", "Your membership does not allow this operation."),
            CommandStatus.NotFound => Problem(StatusCodes.Status404NotFound, $"{resource}-not-found", $"The {resource} was not found."),
            CommandStatus.SlugTaken => Problem(StatusCodes.Status409Conflict, "slug-taken", "The slug is already taken."),
            CommandStatus.SkuTaken => Problem(StatusCodes.Status409Conflict, "sku-taken", "A SKU is already used by another product of the tenant."),
            CommandStatus.IdempotencyKeyReused => Problem(
                StatusCodes.Status422UnprocessableEntity, "idempotency-key-reused", "The Idempotency-Key was already used for a different request."),
            CommandStatus.PreconditionFailed => Problem(
                StatusCodes.Status412PreconditionFailed, "precondition-failed", "The entity tag does not match the current version. Read the resource again."),
            CommandStatus.ConcurrencyConflict => Problem(
                StatusCodes.Status409Conflict, "concurrency-conflict", "A concurrent change was made. Read the current state and try again."),
            CommandStatus.ProductArchived => Problem(StatusCodes.Status409Conflict, "product-archived", "The product is archived. Restore it to change it."),
            CommandStatus.RequirementsNotMet => Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The product does not meet its publication requirements.",
                type: TypeBase + "publication-requirements-not-met",
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["requirements"] = unmet ?? [] }),
            CommandStatus.InvalidTransition => Problem(StatusCodes.Status409Conflict, "invalid-transition", "The change is not possible in the current state."),
            CommandStatus.ParentUnavailable => Problem(StatusCodes.Status409Conflict, "parent-unavailable", "The parent category does not exist or is retired."),
            CommandStatus.CategoryTooDeep => Problem(StatusCodes.Status409Conflict, "category-too-deep", "The taxonomy allows at most five levels."),
            CommandStatus.HasActiveChildren => Problem(
                StatusCodes.Status409Conflict, "category-has-active-children", "Retire the active child categories first."),
            _ => throw new InvalidOperationException($"No problem is defined for {status}."),
        };

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, type: TypeBase + code);
}
