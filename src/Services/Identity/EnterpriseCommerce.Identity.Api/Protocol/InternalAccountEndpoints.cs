using System.Text.Json.Serialization;
using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Domain.Accounts;
using OpenIddict.Validation.AspNetCore;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>
/// The internal account APIs: the lifecycle API (IDENTITY_API_CONTRACT.md section 2.12), called by Admin, and the
/// email verification API (section 2.13, ADR-021), called by Tenant &amp; Merchant. Service tokens only, each with its own
/// scope and allow-list; never routed by the Gateway (it answers <c>404</c> for <c>/internal/**</c> at the edge).
/// </summary>
internal static class InternalAccountEndpoints
{
    /// <summary>The maximum length of an ASP.NET Core Identity user ID column.</summary>
    private const int AccountIdMaxLength = 450;

    public static IEndpointRouteBuilder MapInternalAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var accounts = endpoints.MapGroup("/internal/v1/accounts/{accountId}")
            .RequireAuthorization(IdentityServerSetup.AccountAdministrationPolicy);

        accounts.MapPost("/disable", DisableAsync);
        accounts.MapPost("/reactivate", ReactivateAsync);

        endpoints.MapPost("/internal/v1/accounts/{accountId}/email-verification", VerifyEmailAsync)
            .RequireAuthorization(IdentityServerSetup.AccountVerificationPolicy);
        return endpoints;
    }

    /// <summary>
    /// Answers whether <c>email</c> is the verified address of the active account (ADR-021). The account's own address
    /// is never returned. <c>404</c> for an unknown account; <c>503</c> (exception handler) without the database.
    /// </summary>
    private static async Task<IResult> VerifyEmailAsync(
        string accountId,
        EmailVerificationRequest? request,
        HttpContext httpContext,
        VerifyAccountEmailHandler handler,
        CancellationToken cancellationToken)
    {
        if (IdentityServerSetup.CallingClientOf(httpContext.User) is not { } clientId)
        {
            return Results.Forbid(authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme]);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > AccountIdMaxLength)
        {
            errors["accountId"] = ["The account ID is not valid."];
        }

        if (!AccountEmail.TryNormalize(request?.Email, out _, out var normalizedEmail))
        {
            errors["email"] = ["The email address is not valid."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await handler.HandleAsync(accountId, normalizedEmail, clientId, cancellationToken);
        return result == AccountEmailVerificationResult.NotFound
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The account was not found.")
            : Results.Ok(new EmailVerificationResponse(accountId, result.ToString()));
    }

    /// <summary>
    /// Disables the account. <c>200</c> once the disablement is committed (or the account was already disabled), whatever
    /// the outcome of the post-commit cleanup; <c>503</c> (exception handler) if it could not be committed.
    /// </summary>
    private static async Task<IResult> DisableAsync(
        string accountId,
        AccountStatusRequest? request,
        HttpContext httpContext,
        DisableAccountHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryCreateChange(accountId, request, httpContext, out var change, out var problem))
        {
            return problem;
        }

        return Respond(accountId, await handler.HandleAsync(change, cancellationToken));
    }

    /// <summary>Reactivates the account. Nothing from before the disablement becomes valid again.</summary>
    private static async Task<IResult> ReactivateAsync(
        string accountId,
        AccountStatusRequest? request,
        HttpContext httpContext,
        ReactivateAccountHandler handler,
        CancellationToken cancellationToken)
    {
        if (!TryCreateChange(accountId, request, httpContext, out var change, out var problem))
        {
            return problem;
        }

        return Respond(accountId, await handler.HandleAsync(change, cancellationToken));
    }

    private static bool TryCreateChange(
        string accountId,
        AccountStatusRequest? request,
        HttpContext httpContext,
        out AccountStatusChange change,
        out IResult problem)
    {
        change = null!;

        // The policy guarantees an allowed service client. Checked again so that a misconfiguration never records no caller.
        if (IdentityServerSetup.CallingClientOf(httpContext.User) is not { } clientId)
        {
            problem = Results.Forbid(authenticationSchemes: [OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme]);
            return false;
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > AccountIdMaxLength)
        {
            errors["accountId"] = ["The account ID is not valid."];
        }

        if (request is null || !Enum.TryParse<AccountStatusReason>(request.Reason, ignoreCase: false, out var reason)
            || !AccountLifecycle.IsDefined(reason) || int.TryParse(request.Reason, out _))
        {
            errors["reason"] = [$"The reason must be one of: {string.Join(", ", Enum.GetNames<AccountStatusReason>())}."];
            reason = default;
        }

        if (!AccountLifecycle.IsValidActorReference(request?.ActorReference))
        {
            errors["actorReference"] = [$"The actor reference must be 1 to {AccountLifecycle.ActorReferenceMaxLength} characters of letters, digits, '.', '_', ':' or '-'."];
        }

        if (errors.Count > 0)
        {
            problem = Results.ValidationProblem(errors);
            return false;
        }

        change = new AccountStatusChange(accountId, reason, request!.ActorReference!, clientId);
        problem = Results.Empty;
        return true;
    }

    private static IResult Respond(string accountId, AccountLifecycleResult result) =>
        result.Outcome == AccountLifecycleOutcome.NotFound
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The account was not found.")
            : Results.Ok(new AccountStatusResponse(accountId, result.Status!.Value.ToString(), result.Outcome == AccountLifecycleOutcome.Changed));

    /// <summary>Request body of the lifecycle endpoints.</summary>
    internal sealed record AccountStatusRequest(string? Reason, string? ActorReference);

    /// <summary>Request body of the email verification endpoint. Unknown members are rejected.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed record EmailVerificationRequest(string? Email);

    /// <summary>Response body of the email verification endpoint: <c>Match</c>, <c>Mismatch</c>, <c>Unverified</c> or <c>Inactive</c>.</summary>
    internal sealed record EmailVerificationResponse(string AccountId, string Result);

    /// <summary>Response body: the resulting status, and whether this request changed it.</summary>
    internal sealed record AccountStatusResponse(string AccountId, string Status, bool Changed);
}
