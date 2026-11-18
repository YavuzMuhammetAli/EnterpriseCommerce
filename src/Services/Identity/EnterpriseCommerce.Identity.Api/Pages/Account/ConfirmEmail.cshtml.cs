using EnterpriseCommerce.Identity.Api.Protocol;
using EnterpriseCommerce.Identity.Application.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace EnterpriseCommerce.Identity.Api.Pages.Account;

/// <summary>
/// The email confirmation page (ADR-018 section 1). GET only renders a form with the token; it never consumes it,
/// because email scanners and link previews fetch links automatically. The POST consumes the token.
/// </summary>
[EnableRateLimiting(IdentityServerSetup.CredentialRateLimitPolicy)]
public sealed class ConfirmEmailModel(ConfirmEmailHandler handler) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public EmailConfirmationResult? Result { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Result = await handler.HandleAsync(Token, cancellationToken);

        // The token is never echoed back after a POST.
        Token = null;
        return Page();
    }
}
