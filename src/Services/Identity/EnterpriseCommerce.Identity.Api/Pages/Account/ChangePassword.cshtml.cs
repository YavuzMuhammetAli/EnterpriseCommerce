using System.ComponentModel.DataAnnotations;
using EnterpriseCommerce.Identity.Api.Protocol;
using EnterpriseCommerce.Identity.Application.Accounts;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.Api.Pages.Account;

/// <summary>
/// Authenticated password change (ADR-017 section 7). The account is the one of the current SSO cookie; no request
/// value selects it. The current password is required, and a wrong one counts towards lockout as on the login page.
/// </summary>
/// <remarks>
/// The change raises the security version, which invalidates every session of the user, the current cookie included.
/// Only after the change has committed is the current browser cookie reissued with the new version, so this browser
/// stays signed in to Identity. If reissuing fails, the change stands and the user signs in again: the old cookie no
/// longer passes validation either way. Client (BFF) sessions end at their next refresh and can re-authorize through
/// the reissued cookie without a password prompt.
/// </remarks>
[Authorize]
[EnableRateLimiting(IdentityServerSetup.CredentialRateLimitPolicy)]
public sealed partial class ChangePasswordModel(
    UserManager<ApplicationUser> users,
    ChangePasswordHandler handler,
    TimeProvider timeProvider,
    ILogger<ChangePasswordModel> logger) : PageModel
{
    public const string WrongPassword = "The current password is not correct.";

    [BindProperty]
    public ChangePasswordInput Input { get; set; } = new();

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public bool Changed { get; private set; }

    public bool SignInAgainRequired { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var userId = User.GetClaim(Claims.Subject);
        var user = userId is null ? null : await users.FindByIdAsync(userId);
        if (user is null)
        {
            return Challenge(IdentityConstants.ApplicationScheme);
        }

        if (!string.Equals(Input.NewPassword, Input.ConfirmPassword, StringComparison.Ordinal))
        {
            Errors = ["The new passwords do not match."];
            return Page();
        }

        // The password is not checked while locked out, so guessing cannot continue during lockout.
        if (await users.IsLockedOutAsync(user))
        {
            LogWrongCurrentPassword(logger, user.Id);
            Errors = [WrongPassword];
            return Page();
        }

        if (!await users.CheckPasswordAsync(user, Input.CurrentPassword))
        {
            await users.AccessFailedAsync(user);
            LogWrongCurrentPassword(logger, user.Id);
            Errors = [WrongPassword];
            return Page();
        }

        // The hash just verified. The handler changes the password only while it is still the stored one.
        var result = await handler.HandleAsync(user.Id, user.PasswordHash!, Input.NewPassword, cancellationToken);
        switch (result.Status)
        {
            case PasswordUpdateStatus.PasswordRejected:
                Errors = [PasswordPolicyText.Description];
                return Page();

            case PasswordUpdateStatus.Rejected:
                Errors = ["Your password could not be changed. Try again."];
                return Page();
        }

        Changed = true;
        await ReissueCookieAsync(user.Id, result.SecurityVersion!.Value);
        return Page();
    }

    /// <summary>Runs only after the change committed. A failure leaves the browser signed out, never on the old version.</summary>
    private async Task ReissueCookieAsync(string userId, long securityVersion)
    {
        // The cookie was validated earlier in this request; its sign-in time is kept so the absolute lifetime is unchanged.
        var issued = (await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme)).Properties?.IssuedUtc;

        try
        {
            await HttpContext.SignInAsync(
                IdentityConstants.ApplicationScheme,
                IdentityServerSetup.CreateSsoPrincipal(userId, securityVersion),
                new AuthenticationProperties { IsPersistent = false, IssuedUtc = issued ?? timeProvider.GetUtcNow() });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately broad: whatever failed, the committed change stands and the user signs in again.
            LogCookieReissueFailed(logger, exception, userId);
            SignInAgainRequired = true;
            await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    }

    [LoggerMessage(EventId = 5110, Level = LogLevel.Warning, Message = "Security event PasswordChangeFailed: wrong current password for subject {Subject}")]
    private static partial void LogWrongCurrentPassword(ILogger logger, string subject);

    [LoggerMessage(EventId = 5111, Level = LogLevel.Error, Message = "Password changed for subject {Subject}, but the browser cookie could not be reissued; the user must sign in again")]
    private static partial void LogCookieReissueFailed(ILogger logger, Exception exception, string subject);

    public sealed class ChangePasswordInput
    {
        [MaxLength(512)]
        [DataType(DataType.Password)]
        public string CurrentPassword { get; set; } = string.Empty;

        [MaxLength(512)]
        [DataType(DataType.Password)]
        public string NewPassword { get; set; } = string.Empty;

        [MaxLength(512)]
        [DataType(DataType.Password)]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
