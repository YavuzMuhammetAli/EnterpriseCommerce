namespace EnterpriseCommerce.TenantMerchant.Api.Tenants;

/// <summary>
/// RFC 9457 problem responses of the Tenant &amp; Merchant API. The <c>type</c> URIs follow the placeholder scheme of
/// IDENTITY_API_CONTRACT.md section 1.4; the final scheme is an open item there.
/// </summary>
internal static class Problems
{
    private const string TypeBase = "https://errors.enterprisecommerce.example/tenant-merchant/";

    public static IResult Validation(IReadOnlyDictionary<string, string[]> errors) =>
        Results.ValidationProblem(
            errors.ToDictionary(),
            title: "One or more validation errors occurred.",
            type: TypeBase + "validation-failed");

    public static IResult TenantNotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The tenant was not found.", type: TypeBase + "tenant-not-found");

    public static IResult PermissionDenied() =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Your membership does not allow this operation.",
            type: TypeBase + "permission-denied");

    public static IResult SlugTaken() =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The tenant slug is already taken.", type: TypeBase + "tenant-slug-taken");

    public static IResult IdempotencyKeyReused() =>
        Results.Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "The Idempotency-Key was already used for a different request.",
            type: TypeBase + "idempotency-key-reused");
}
