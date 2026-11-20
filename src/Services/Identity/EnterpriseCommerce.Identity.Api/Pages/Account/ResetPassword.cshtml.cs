using System.ComponentModel.DataAnnotations;
using EnterpriseCommerce.Identity.Api.Protocol;
using EnterpriseCommerce.Identity.Application.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace EnterpriseCommerce.Identity.Api.Pages.Account;

/// <summary>
/// Password reset confirmation (ADR-018 section 1). GET only renders the form with the token; it never consumes it,
/// because email scanners and link previews fetch links automatically. The POST resets the password. Success does not
/// sign the user in: every session of the user ends, and the user signs in again with the new password.
/// </summary>
[EnableRateLimiting(IdentityServerSetup.CredentialRateLimitPolicy)]
public sealed class ResetPasswordModel(ResetPasswordHandler handler) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public ResetPasswordInput Input { get; set; } = new();

    public PasswordUpdateStatus? Result { get; private set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(Input.Password, Input.ConfirmPassword, StringComparison.Ordinal))
        {
            Errors = ["The passwords do not match."];
            return Page();
        }

        var result = await handler.HandleAsync(Token, Input.Password, cancellationToken);

        if (result.Status == PasswordUpdateStatus.PasswordRejected)
        {
            // The token is still unconsumed: it stays in the form so that the user can choose another password.
            Errors = [PasswordPolicyText.Description];
            return Page();
        }

        // The token is never echoed back once it is consumed or rejected.
        Result = result.Status;
        Token = null;

        if (result.Status == PasswordUpdateStatus.Succeeded)
        {
            // Any Identity cookie in this browser is stale now; clear it instead of waiting for its next validation.
            await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }

        return Page();
    }

    public sealed class ResetPasswordInput
    {
        [MaxLength(512)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [MaxLength(512)]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}

/// <summary>The user-facing description of the password policy (configured in Identity.Infrastructure).</summary>
internal static class PasswordPolicyText
{
    public const string Description =
        "The password must have at least 12 characters, with an upper-case letter, a lower-case letter, a digit and a symbol.";
}
