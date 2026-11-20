using EnterpriseCommerce.Identity.Api.Pages.Connect;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace EnterpriseCommerce.Identity.Api.Pages.Account;

/// <summary>
/// Signs the browser out of Identity itself, for users who are on an Identity page without a client application
/// (for example after a password change). GET only renders the form; the POST is protected by antiforgery and the
/// Fetch Metadata and Origin checks. The return URL must be local; anything else goes to the signed-out page.
/// Client sessions end through <c>/connect/revoke</c> and <c>/connect/endsession</c>.
/// </summary>
public sealed class LogoutModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : EndSessionModel.SignedOutPage);
    }
}
