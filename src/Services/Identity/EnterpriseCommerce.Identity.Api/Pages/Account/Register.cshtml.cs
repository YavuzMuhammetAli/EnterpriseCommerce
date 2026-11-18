using System.ComponentModel.DataAnnotations;
using EnterpriseCommerce.Identity.Api.Protocol;
using EnterpriseCommerce.Identity.Application.Accounts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace EnterpriseCommerce.Identity.Api.Pages.Account;

/// <summary>
/// The Identity-hosted registration page (ADR-017 section 9). The outcome page is the same whether or not the email
/// already belongs to an account; only input errors, which never depend on account existence, are shown here.
/// </summary>
[EnableRateLimiting(IdentityServerSetup.CredentialRateLimitPolicy)]
public sealed class RegisterModel(RegisterAccountHandler handler) : PageModel
{
    [BindProperty]
    public RegisterInput Input { get; set; } = new();

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

        var result = await handler.HandleAsync(Input.Email, Input.Password, cancellationToken);
        if (!result.Accepted)
        {
            Errors = Describe(result.Errors);
            return Page();
        }

        return RedirectToPage("/Account/CheckEmail");
    }

    private static List<string> Describe(IReadOnlyList<string> codes)
    {
        var messages = new List<string>();
        if (codes.Contains(RegisterAccountHandler.InvalidEmail))
        {
            messages.Add("Enter a valid email address.");
        }

        if (codes.Any(code => code != RegisterAccountHandler.InvalidEmail))
        {
            messages.Add("The password must have at least 12 characters, with an upper-case letter, a lower-case letter, a digit and a symbol.");
        }

        return messages;
    }

    public sealed class RegisterInput
    {
        [Required]
        [MaxLength(AccountEmail.MaxLength)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MaxLength(512)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Required]
        [MaxLength(512)]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
