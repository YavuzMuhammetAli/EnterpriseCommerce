using System.ComponentModel.DataAnnotations;
using EnterpriseCommerce.Identity.Api.Protocol;
using EnterpriseCommerce.Identity.Application.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace EnterpriseCommerce.Identity.Api.Pages.Account;

/// <summary>"Forgot password". Always the same neutral outcome page, whatever the address (ADR-018 section 2).</summary>
[EnableRateLimiting(IdentityServerSetup.CredentialRateLimitPolicy)]
public sealed class ForgotPasswordModel(RequestPasswordResetHandler handler) : PageModel
{
    [BindProperty]
    [Required]
    [MaxLength(AccountEmail.MaxLength)]
    public string Email { get; set; } = string.Empty;

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            await handler.HandleAsync(Email, cancellationToken);
        }

        return RedirectToPage("/Account/ResetRequested");
    }
}
