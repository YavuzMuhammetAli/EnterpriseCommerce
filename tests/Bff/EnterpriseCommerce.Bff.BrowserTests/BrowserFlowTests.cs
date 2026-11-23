using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Playwright;

namespace EnterpriseCommerce.Bff.BrowserTests;

/// <summary>
/// The BFF flows in real browser engines (ADR-016 section 8). Each case uses a fresh browser context and user.
/// Playwright WebKit is not real Safari: the real Safari (macOS, iOS) gate remains open.
/// </summary>
public sealed class BrowserFlowTests(BrowserEnvironment environment) : IClassFixture<BrowserEnvironment>
{
    public static TheoryData<string> Engines => ["chromium", "firefox", "webkit"];

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Login_SetsTheAcceptedCookies_AndNoTokenReachesTheBrowser(string engine)
    {
        environment.RequireBrowsers();
        var bff = environment.StorefrontHost;
        await using var context = await environment.NewContextAsync(engine);
        var page = await context.NewPageAsync();
        var bffBodies = new ConcurrentBag<string>();
        var bffResponses = new ConcurrentBag<IResponse>();
        page.Response += (_, response) =>
        {
            if (response.Url.StartsWith(bff.Origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            {
                bffResponses.Add(response);
            }

            if (response.Url.StartsWith(bff.Origin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal) && response.Status is < 300 or >= 400)
            {
                bffBodies.Add(response.Url + " " + string.Join(" ", response.Headers.Select(h => h.Key + ": " + h.Value)));
            }
        };
        var email = await environment.CreateUserAsync();

        // The correlation cookies exist while Identity's login page is shown.
        await page.GotoAsync(bff.Url("/bff/login?returnUrl=%2F").AbsoluteUri);
        await page.WaitForURLAsync(url => url.Contains("/account/login", StringComparison.Ordinal));
        var setCookies = await SetCookiesAsync(bffResponses);
        var correlation = (await context.CookiesAsync([bff.Origin.AbsoluteUri])).Where(c => c.Name.StartsWith("__Host-ec-storefront-oidc-", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, correlation.Count);
        Assert.All(correlation, cookie =>
        {
            Assert.True(cookie.Secure);
            Assert.True(cookie.HttpOnly);
            AssertSameSite(engine, SameSiteAttribute.Lax, cookie.SameSite);
            Assert.Equal("/", cookie.Path);
        });
        var correlationHeaders = setCookies.Where(c => c.StartsWith("__Host-ec-storefront-oidc-", StringComparison.Ordinal)).ToList();
        if (ReportsRedirectSetCookieHeaders(engine))
        {
            Assert.Equal(2, correlationHeaders.Count);
        }

        Assert.All(correlationHeaders, c => Assert.Contains("samesite=lax", c.ToLowerInvariant(), StringComparison.Ordinal));

        await page.FillAsync("input[name='Input.Email']", email);
        await page.FillAsync("input[name='Input.Password']", BrowserEnvironment.Password);
        await page.PressAsync("input[name='Input.Password']", "Enter");
        await page.WaitForURLAsync(bff.Url("/").AbsoluteUri);

        var cookies = await context.CookiesAsync([bff.Origin.AbsoluteUri]);
        setCookies = await SetCookiesAsync(bffResponses);
        var session = Assert.Single(cookies, c => c.Name == "__Host-Http-ec-storefront");
        Assert.True(session.Secure);
        Assert.True(session.HttpOnly);
        AssertSameSite(engine, SameSiteAttribute.Strict, session.SameSite);
        var sessionHeaders = setCookies.Where(c => c.StartsWith("__Host-Http-ec-storefront=", StringComparison.Ordinal)).ToList();
        if (ReportsRedirectSetCookieHeaders(engine))
        {
            Assert.NotEmpty(sessionHeaders);
        }

        Assert.All(sessionHeaders, c => Assert.Contains("samesite=strict", c.ToLowerInvariant(), StringComparison.Ordinal));
        Assert.Equal("/", session.Path);
        Assert.Equal(-1, session.Expires);
        Assert.DoesNotContain(cookies, c => c.Name.Contains("-oidc-", StringComparison.Ordinal));

        // Same-origin script calls carry the Strict session cookie even after the cross-site login redirects.
        var user = await UserAsync(page);
        Assert.Equal(200, user.GetProperty("status").GetInt32());
        Assert.Equal(await environment.UserIdAsync(email), JsonDocument.Parse(user.GetProperty("body").GetString()!).RootElement.GetProperty("sub").GetString());

        // Script sees no session cookie and no token anywhere.
        var visible = await page.EvaluateAsync<string>("document.cookie");
        Assert.DoesNotContain("__Host-Http-ec-storefront", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", visible, StringComparison.Ordinal);
        Assert.Equal(0, await page.EvaluateAsync<int>("localStorage.length + sessionStorage.length"));
        Assert.DoesNotContain("eyJ", user.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(bffBodies, entry => entry.Contains("eyJ", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task ACrossSiteFormPost_CannotLogTheUserOut(string engine)
    {
        environment.RequireBrowsers();
        var bff = environment.StorefrontHost;
        await using var context = await environment.NewContextAsync(engine);
        var page = await context.NewPageAsync();
        await environment.SignInAsync(page, bff, await environment.CreateUserAsync());
        Assert.Equal(200, (await UserAsync(page)).GetProperty("status").GetInt32());

        var attacker = await context.NewPageAsync();
        var forged = await attacker.RunAndWaitForResponseAsync(
            () => attacker.GotoAsync(environment.AttackerOrigin.AbsoluteUri, new PageGotoOptions { WaitUntil = WaitUntilState.Commit }),
            response => response.Url.EndsWith("/bff/logout", StringComparison.Ordinal));

        Assert.Equal(403, forged.Status);
        Assert.Equal(200, (await UserAsync(page)).GetProperty("status").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Logout_EndsTheSession_AndHandsOverToIdentity(string engine)
    {
        environment.RequireBrowsers();
        var bff = environment.StorefrontHost;
        await using var context = await environment.NewContextAsync(engine);
        var page = await context.NewPageAsync();
        await environment.SignInAsync(page, bff, await environment.CreateUserAsync());
        await UserAsync(page);

        var logout = await page.EvaluateAsync<JsonElement>("bff.post('/bff/logout')");
        Assert.Equal(200, logout.GetProperty("status").GetInt32());
        var endSession = JsonDocument.Parse(logout.GetProperty("body").GetString()!).RootElement.GetProperty("endSessionUrl").GetString()!;
        Assert.DoesNotContain("id_token_hint", endSession, StringComparison.Ordinal);
        Assert.DoesNotContain(await context.CookiesAsync([bff.Origin.AbsoluteUri]), c => c.Name == "__Host-Http-ec-storefront");

        var confirmation = await page.GotoAsync(endSession);
        Assert.Equal(200, confirmation!.Status);
        Assert.StartsWith(environment.IdentityIssuer.GetLeftPart(UriPartial.Authority), page.Url, StringComparison.Ordinal);

        await page.GotoAsync(bff.Url("/").AbsoluteUri);
        var user = await UserAsync(page);
        Assert.Equal(401, user.GetProperty("status").GetInt32());
        Assert.Contains("/bff/login", user.GetProperty("login").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task AnIdleSession_Expires(string engine)
    {
        environment.RequireBrowsers();
        var bff = environment.ShortIdleHost;
        await using var context = await environment.NewContextAsync(engine);
        var page = await context.NewPageAsync();
        await environment.SignInAsync(page, bff, await environment.CreateUserAsync());
        Assert.Equal(200, (await UserAsync(page)).GetProperty("status").GetInt32());

        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        Assert.Equal(401, (await UserAsync(page)).GetProperty("status").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task AnExternalReturnUrl_LandsOnTheFrontEnd(string engine)
    {
        environment.RequireBrowsers();
        var bff = environment.StorefrontHost;
        await using var context = await environment.NewContextAsync(engine);
        var page = await context.NewPageAsync();

        await environment.SignInAsync(page, bff, await environment.CreateUserAsync(), returnUrl: "https://evil.example/steal");

        Assert.Equal(bff.Url("/").AbsoluteUri, page.Url);
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public async Task AfterAPasswordChange_TheBffReauthorizesSilently(string engine)
    {
        environment.RequireBrowsers();
        var bff = environment.StorefrontHost;
        await using var context = await environment.NewContextAsync(engine);
        var page = await context.NewPageAsync();
        await environment.SignInAsync(page, bff, await environment.CreateUserAsync());
        var before = (await context.CookiesAsync([bff.Origin.AbsoluteUri])).Single(c => c.Name == "__Host-Http-ec-storefront").Value;

        // Change the password on Identity's page in the same browser.
        await page.GotoAsync(new Uri(environment.IdentityIssuer, "/account/change-password").AbsoluteUri);
        await page.FillAsync("input[name='Input.CurrentPassword']", BrowserEnvironment.Password);
        await page.FillAsync("input[name='Input.NewPassword']", BrowserEnvironment.NewPassword);
        await page.FillAsync("input[name='Input.ConfirmPassword']", BrowserEnvironment.NewPassword);
        var change = await page.RunAndWaitForResponseAsync(
            () => page.PressAsync("input[name='Input.ConfirmPassword']", "Enter"),
            response => response.Request.Method == "POST" && response.Url.Contains("/account/change-password", StringComparison.Ordinal));
        Assert.True(change.Status is 200 or 302, change.Status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.WaitForLoadStateAsync();

        // The BFF's next refresh is rejected: the session ends and the front end is told to sign in again.
        bff.Time.Advance(TimeSpan.FromMinutes(9));
        await page.GotoAsync(bff.Url("/").AbsoluteUri);
        var expired = await UserAsync(page);
        Assert.Equal(401, expired.GetProperty("status").GetInt32());

        // Silent re-authorization through Identity's reissued SSO cookie: no login page.
        var visited = new ConcurrentBag<string>();
        page.FrameNavigated += (_, frame) => visited.Add(frame.Url);
        await page.GotoAsync(bff.Url("/bff/login?silent=1&returnUrl=%2F").AbsoluteUri);
        await page.WaitForURLAsync(bff.Url("/").AbsoluteUri);

        Assert.DoesNotContain(visited, url => url.Contains("/account/login", StringComparison.Ordinal));
        var after = (await context.CookiesAsync([bff.Origin.AbsoluteUri])).Single(c => c.Name == "__Host-Http-ec-storefront").Value;
        Assert.NotEqual(before, after);
        Assert.Equal(200, (await UserAsync(page)).GetProperty("status").GetInt32());
    }

    /// <summary>
    /// Playwright's WebKit build for Windows does not implement SameSite: its cookie store reports None for every
    /// cookie. The integration tests verify every Set-Cookie attribute on the wire; on that engine the CSRF defence
    /// rests on Fetch Metadata, Origin and the antiforgery token, which <see cref="ACrossSiteFormPost_CannotLogTheUserOut"/>
    /// proves. Real Safari is a separate, still open gate (ADR-016 section 8).
    /// </summary>
    private static bool IsWindowsWebKit(string engine) => engine == "webkit" && OperatingSystem.IsWindows();

    /// <summary>
    /// Whether the engine reports the Set-Cookie headers of redirect responses through Playwright. The BFF sets its
    /// cookies on redirects (<c>/bff/login</c> and <c>/bff/callback</c> answer 302), so the raw-header assertions need it.
    /// </summary>
    /// <remarks>
    /// WebKit does not report them on any operating system. On Windows that was already known. On Linux, a probe with
    /// Playwright 1.64.1 (WebKit 2370, image <c>mcr.microsoft.com/playwright/dotnet:v1.64.1-noble</c>, 2026-11-23)
    /// found Set-Cookie on a 200 response but on none of four 302 responses (same-origin, form POST, cross-site chain,
    /// cross-site form_post), through both <c>HeadersArrayAsync</c> and <c>AllHeadersAsync</c>, while the cookie store
    /// kept every cookie with Secure, HttpOnly and SameSite=Strict. For WebKit, the cookie-store assertions remain, a
    /// header that is reported must still carry the expected SameSite, and LoginFlowTests verify every Set-Cookie
    /// attribute on the wire for all engines. Chromium and Firefox must report the headers.
    /// </remarks>
    private static bool ReportsRedirectSetCookieHeaders(string engine) => engine != "webkit";

    private static void AssertSameSite(string engine, SameSiteAttribute expected, SameSiteAttribute? actual)
    {
        if (!IsWindowsWebKit(engine))
        {
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>The raw Set-Cookie headers the BFF sent (one header per cookie).</summary>
    private static async Task<List<string>> SetCookiesAsync(IEnumerable<IResponse> responses)
    {
        var values = new List<string>();
        foreach (var response in responses.ToList())
        {
            values.AddRange((await response.HeadersArrayAsync())
                .Where(header => header.Name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase))
                .SelectMany(header => header.Value.Split('\n')));
        }

        return values;
    }

    private static Task<JsonElement> UserAsync(IPage page) => page.EvaluateAsync<JsonElement>("bff.user()");
}
