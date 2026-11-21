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

    public static IResult MembershipNotFound() =>
        Problem(StatusCodes.Status404NotFound, "membership-not-found", "The membership was not found.");

    public static IResult SelfModificationForbidden() =>
        Problem(StatusCodes.Status403Forbidden, "self-modification-forbidden", "You cannot change your own membership.");

    public static IResult InvalidTransition() =>
        Problem(StatusCodes.Status409Conflict, "invalid-transition", "The change is not possible in the current state.");

    public static IResult LastActiveOwner() =>
        Problem(StatusCodes.Status409Conflict, "last-active-owner", "The tenant must keep at least one active owner.");

    public static IResult OwnerRoleReserved() =>
        Problem(StatusCodes.Status422UnprocessableEntity, "owner-role-reserved", "The MerchantOwner role is granted only by tenant provisioning.");

    public static IResult ConcurrencyConflict() =>
        Problem(StatusCodes.Status409Conflict, "concurrency-conflict", "A concurrent change was made. Read the current state and try again.");

    public static IResult InvitationNotFound() =>
        Problem(StatusCodes.Status404NotFound, "invitation-not-found", "The invitation was not found.");

    public static IResult InvitationAlreadyPending(Guid? invitationId)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (invitationId is { } id)
        {
            extensions["invitationId"] = id;
        }

        return Results.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "The address already has a pending invitation. Cancel it to invite again.",
            type: TypeBase + "invitation-already-pending",
            extensions: extensions);
    }

    public static IResult InvitationLimitReached() =>
        Problem(StatusCodes.Status409Conflict, "invitation-limit-reached", "The tenant has the maximum number of open invitations.");

    public static IResult InvitationNotPending() =>
        Problem(StatusCodes.Status409Conflict, "invitation-not-pending", "The invitation is no longer pending.");

    public static IResult InvitationExpired() =>
        Problem(StatusCodes.Status410Gone, "invitation-expired", "The invitation has expired.");

    public static IResult InvitationEmailMismatch() =>
        Problem(StatusCodes.Status403Forbidden, "invitation-email-mismatch", "The invitation was sent to a different email address.");

    public static IResult InvitationEmailUnverified() =>
        Problem(StatusCodes.Status403Forbidden, "invitation-email-unverified", "Confirm your email address before accepting the invitation.");

    public static IResult InvitationAccountIneligible() =>
        Problem(StatusCodes.Status403Forbidden, "invitation-account-ineligible", "This account cannot accept invitations.");

    public static IResult AlreadyAMember() =>
        Problem(StatusCodes.Status409Conflict, "already-a-member", "You already have a membership in this tenant.");

    private static IResult Problem(int status, string code, string title) =>
        Results.Problem(statusCode: status, title: title, type: TypeBase + code);
}
