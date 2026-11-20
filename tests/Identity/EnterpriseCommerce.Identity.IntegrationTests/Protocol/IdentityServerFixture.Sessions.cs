using System.Net;
using System.Text.RegularExpressions;
using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using EnterpriseCommerce.Identity.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace EnterpriseCommerce.Identity.IntegrationTests.Protocol;

/// <summary>Helpers for logout, revocation and password flows (Phase 2.3 S4).</summary>
public partial class IdentityServerFixture
{
    public const string NewPassword = "Different-Staple-Lamp-7";

    /// <summary>A browser whose cookies a test can snapshot and restore, to replay an old cookie.</summary>
    public HttpClient CreateBrowser(CookieJar jar) => Factory.CreateDefaultClient(Issuer, jar);

    /// <summary>Revokes a token at <c>/connect/revoke</c>, authenticated as <paramref name="client"/>.</summary>
    public async Task<HttpStatusCode> RevokeAsync(TestClient client, string token, string? tokenTypeHint = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["token"] = token,
            ["client_id"] = client.ClientId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = client.CreateAssertion(Issuer),
        };
        if (tokenTypeHint is not null)
        {
            parameters["token_type_hint"] = tokenTypeHint;
        }

        using var backChannel = CreateBackChannel();
        using var response = await backChannel.PostAsync(new Uri("/connect/revoke", UriKind.Relative), new FormUrlEncodedContent(parameters), TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>Calls logout-all with an optional bearer token and optional browser (cookies).</summary>
    public async Task<HttpStatusCode> LogoutAllAsync(string? accessToken, HttpClient? browser = null)
    {
        using var ownClient = browser is null ? CreateBackChannel() : null;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout-all");
        if (accessToken is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        }

        using var response = await (browser ?? ownClient!).SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>Whether the browser's Identity SSO cookie is currently accepted (an authenticated page renders).</summary>
    public async Task<bool> IsSignedInAsync(HttpClient browser)
    {
        using var response = await browser.GetAsync("/account/change-password", TestContext.Current.CancellationToken);
        return response.StatusCode == HttpStatusCode.OK;
    }

    /// <summary>Opens the end-session confirmation page and submits its form by POST.</summary>
    public async Task<HttpResponseMessage> SubmitEndSessionAsync(HttpClient browser, string query, bool sameOrigin = true, bool withAntiforgery = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var page = await browser.GetStringAsync("/connect/endsession" + query, ct);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match input in HiddenInput().Matches(page))
        {
            fields[WebUtility.HtmlDecode(NameAttribute().Match(input.Value).Groups[1].Value)] =
                WebUtility.HtmlDecode(ValueAttribute().Match(input.Value).Groups[1].Value);
        }

        if (!withAntiforgery)
        {
            fields.Remove("__RequestVerificationToken");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/connect/endsession") { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("Sec-Fetch-Site", sameOrigin ? "same-origin" : "cross-site");
        return await browser.SendAsync(request, ct);
    }

    public static string EndSessionQuery(TestClient client, string? idTokenHint, string state) =>
        "?client_id=" + Uri.EscapeDataString(client.ClientId)
        + "&post_logout_redirect_uri=" + Uri.EscapeDataString(client.PostLogoutRedirectUri)
        + "&state=" + state
        + (idTokenHint is null ? string.Empty : "&id_token_hint=" + Uri.EscapeDataString(idTokenHint));

    /// <summary>Requests a password reset through the page and returns the reset token from the captured email.</summary>
    public async Task<string> RequestResetTokenAsync(string email)
    {
        using var browser = CreateBrowser();
        using var response = await SubmitPageFormAsync(browser, "/account/forgot-password", new Dictionary<string, string> { ["Email"] = email });
        Assert.Equal("/account/reset-requested", response.Headers.Location?.OriginalString);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var message = Dispatcher.MessagesTo(email).LastOrDefault(m => m.Purpose == SecurityTokenPurpose.PasswordReset);
            if (message is not null)
            {
                Assert.Equal("/account/reset-password", message.Link.AbsolutePath);
                return CapturingSecurityEmailDispatcher.TokenOf(message);
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"No password reset email to {email}.");
    }

    /// <summary>Submits the reset page form with the token and a new password.</summary>
    public Task<HttpResponseMessage> SubmitResetAsync(HttpClient browser, string token, string newPassword = NewPassword) =>
        SubmitPageFormAsync(browser, "/account/reset-password?token=" + Uri.EscapeDataString(token), new Dictionary<string, string>
        {
            ["Token"] = token,
            ["Input.Password"] = newPassword,
            ["Input.ConfirmPassword"] = newPassword,
        });

    /// <summary>Submits the password change page of a signed-in browser.</summary>
    public Task<HttpResponseMessage> SubmitChangeAsync(HttpClient browser, string currentPassword, string newPassword = NewPassword) =>
        SubmitPageFormAsync(browser, "/account/change-password", new Dictionary<string, string>
        {
            ["Input.CurrentPassword"] = currentPassword,
            ["Input.NewPassword"] = newPassword,
            ["Input.ConfirmPassword"] = newPassword,
        });

    /// <summary>Whether the password signs in on the login page.</summary>
    public async Task<bool> PasswordWorksAsync(string email, string password)
    {
        using var browser = CreateBrowser();
        using var response = await SubmitLoginAsync(browser, email, password);
        return response.StatusCode == HttpStatusCode.Redirect;
    }

    public async Task<long?> SecurityVersionOfAsync(string userId) =>
        await IdentityTestServices.ReadSecurityVersionAsync(Services, userId);

    /// <summary>The OpenIddict statuses of the user's authorizations.</summary>
    public async Task<List<string?>> AuthorizationStatusesAsync(string userId)
    {
        await using var scope = Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var statuses = new List<string?>();
        await foreach (var authorization in manager.FindBySubjectAsync(userId, TestContext.Current.CancellationToken))
        {
            statuses.Add(await manager.GetStatusAsync(authorization, TestContext.Current.CancellationToken));
        }

        return statuses;
    }

    /// <summary>Whether the reset token with this raw value was consumed.</summary>
    public async Task<bool> IsConsumedAsync(string rawToken)
    {
        var hash = Application.Security.SecurityTokenCodec.Hash(rawToken);
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        return await context.AccountSecurityTokens.AsNoTracking().AnyAsync(t => t.TokenHash == hash && t.ConsumedAt != null, TestContext.Current.CancellationToken);
    }

    public async Task<ApplicationUser> ReadUserAsync(string userId)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        return await context.Users.AsNoTracking().SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
    }

    public async Task<int> DeliveryCountAsync(string userId)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IdentityServiceDbContext>();
        return await context.SecurityEmailDeliveries.AsNoTracking().CountAsync(d => d.UserId == userId, TestContext.Current.CancellationToken);
    }

    public async Task DisableAsync(string userId)
    {
        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        user!.Status = Domain.Accounts.AccountStatus.Disabled;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    [GeneratedRegex("<input[^>]*type=\"hidden\"[^>]*>")]
    private static partial Regex HiddenInput();

    [GeneratedRegex("name=\"([^\"]+)\"")]
    private static partial Regex NameAttribute();

    [GeneratedRegex("value=\"([^\"]*)\"")]
    private static partial Regex ValueAttribute();
}
