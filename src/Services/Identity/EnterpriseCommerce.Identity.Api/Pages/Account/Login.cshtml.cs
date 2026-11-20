using System.ComponentModel.DataAnnotations;
using EnterpriseCommerce.Identity.Api.Protocol;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace EnterpriseCommerce.Identity.Api.Pages.Account;

/// <summary>
/// The Identity-hosted login page (ADR-017 section 9, PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.2).
/// The only place where a password is entered. Every failure shows the same generic message, so the page never
/// reveals whether an account exists, is locked or is disabled.
/// </summary>
[EnableRateLimiting(IdentityServerSetup.CredentialRateLimitPolicy)]
public sealed partial class LoginModel(
    UserManager<ApplicationUser> users,
    SecurityStateReader securityState,
    ILogger<LoginModel> logger) : PageModel
{
    public const string GenericFailure = "Invalid email or password.";

    private static readonly Lazy<string> DummyPasswordHash = new(() =>
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Guid.NewGuid().ToString()));

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; private set; }

    public bool ShowConfirmEmailHint { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Failure();
        }

        var user = await users.FindByEmailAsync(Input.Email);
        if (user is null)
        {
            // Equalize timing with the existing-account path.
            users.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DummyPasswordHash.Value, Input.Password);
            Record("failed");
            return Failure();
        }

        if (await users.IsLockedOutAsync(user))
        {
            // The password is not checked while locked, so brute force cannot continue during lockout.
            LogLockedOutAttempt(logger, user.Id);
            Record("locked_out");
            return Failure();
        }

        // Progressive per-account delay, growing with consecutive failures (capped).
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(user.AccessFailedCount * 250, 2000)), cancellationToken);

        if (!await users.CheckPasswordAsync(user, Input.Password))
        {
            await users.AccessFailedAsync(user);
            LogLoginFailed(logger, user.Id);
            Record("failed");
            return Failure();
        }

        await users.ResetAccessFailedCountAsync(user);

        if (user.Status != AccountStatus.Active)
        {
            LogLoginFailed(logger, user.Id);
            Record("not_allowed");
            return Failure();
        }

        if (!user.EmailConfirmed)
        {
            // Shown only after a correct password, so it reveals nothing to someone who does not know it.
            ShowConfirmEmailHint = true;
            Record("email_unconfirmed");
            return Page();
        }

        var state = await securityState.ReadAsync(user.Id, cancellationToken);
        if (state.SecurityVersion is not { } version)
        {
            throw new SecurityStateUnavailableException("The security-version record of the user is missing.");
        }

        await HttpContext.SignInAsync(
            IdentityConstants.ApplicationScheme,
            IdentityServerSetup.CreateSsoPrincipal(user.Id, version),
            new AuthenticationProperties { IsPersistent = false, IssuedUtc = DateTimeOffset.UtcNow });
        LogLoginSucceeded(logger, user.Id);
        Record("succeeded");

        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }

    private static void Record(string outcome) =>
        IdentityMetrics.AuthenticationAttempts.Add(1, IdentityMetrics.Tag("outcome", outcome));

    private PageResult Failure()
    {
        ErrorMessage = GenericFailure;
        return Page();
    }

    [LoggerMessage(EventId = 5100, Level = LogLevel.Information, Message = "Security event LoginSucceeded for subject {Subject}")]
    private static partial void LogLoginSucceeded(ILogger logger, string subject);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Warning, Message = "Security event LoginFailed for subject {Subject}")]
    private static partial void LogLoginFailed(ILogger logger, string subject);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning, Message = "Security event LoginAttemptWhileLockedOut for subject {Subject}")]
    private static partial void LogLockedOutAttempt(ILogger logger, string subject);

    public sealed class LoginInput
    {
        [Required]
        [EmailAddress]
        [MaxLength(256)]
        public string Email { get; set; } = string.Empty;

        [Required]
        [MaxLength(512)]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;
    }
}
