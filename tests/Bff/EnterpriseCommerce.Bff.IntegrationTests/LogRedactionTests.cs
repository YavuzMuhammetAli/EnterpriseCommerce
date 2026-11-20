using System.Text.RegularExpressions;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>The environment with Verbose logs of Identity captured as well.</summary>
public sealed class LoggingBffTestEnvironment : BffTestEnvironment
{
    public LoggingBffTestEnvironment() => IdentityLogSink = Sink;

    public CapturingSink Sink { get; } = new();
}

/// <summary>
/// No secret ever reaches a log (ADR-017 section 11): every BFF flow runs with Verbose logging of the BFF and Identity,
/// then the captured events are searched for every token, code, assertion, verifier, cookie and session key seen.
/// </summary>
public sealed partial class LogRedactionTests(LoggingBffTestEnvironment environment) : IClassFixture<LoggingBffTestEnvironment>
{
    [Fact]
    public async Task AllBffFlows_NeverLogSecrets()
    {
        environment.RequireAvailable();
        var bffSink = new CapturingSink();
        var bff = environment.CreateBff(environment.Storefront, options => options.LogSink = bffSink);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();
        var secrets = new HashSet<string>(StringComparer.Ordinal) { BffTestEnvironment.Password, BffTestEnvironment.NewPassword };

        async Task CollectAsync()
        {
            if (await bff.TokensOfAsync(browser) is { } snapshot)
            {
                secrets.Add(snapshot.Tokens.AccessToken);
                secrets.Add(snapshot.Tokens.RefreshToken!);
                secrets.Add(snapshot.Tokens.IdToken!);
            }

            foreach (var cookie in browser.Cookies.GetAllCookies().Where(c => c.Value.Length >= 16))
            {
                secrets.Add(cookie.Value);
            }
        }

        // Login, refresh, an unknown refresh outcome, password change and silent re-authorization, logout-all, logout.
        await environment.SignInAsync(browser, bff, email);
        await CollectAsync();
        bff.Time.Advance(TimeSpan.FromMinutes(9));
        await BffTestEnvironment.UserAsync(browser, bff);
        await CollectAsync();
        await BffTestEnvironment.ChangePasswordAsync(browser);
        await CollectAsync();
        bff.Time.Advance(TimeSpan.FromMinutes(9));
        await BffTestEnvironment.UserAsync(browser, bff);
        await browser.NavigateAsync(bff.Url("/bff/login?silent=1&returnUrl=%2F"));
        await CollectAsync();
        bff.Channel.Fault = RefreshFault.LoseResponse;
        bff.Time.Advance(TimeSpan.FromMinutes(9));
        await BffTestEnvironment.UserAsync(browser, bff);
        bff.Channel.Fault = RefreshFault.None;
        await BffTestEnvironment.PostAsync(browser, bff, "/bff/logout-all");
        await environment.SignInAsync(browser, bff, email, extraQuery: string.Empty);
        await BffTestEnvironment.SubmitLoginAsync(browser, await browser.NavigateAsync(bff.Url("/bff/login")), email, BffTestEnvironment.NewPassword);
        await CollectAsync();
        await BffTestEnvironment.PostAsync(browser, bff, "/bff/logout");

        foreach (var response in browser.Transcript)
        {
            var query = QueryHelpers.ParseQuery(response.Url.Query);
            foreach (var name in new[] { "code", "state", "nonce", "code_challenge" })
            {
                if (query.TryGetValue(name, out var value) && value.ToString().Length >= 16)
                {
                    secrets.Add(value.ToString());
                }
            }
        }

        foreach (var body in bff.Channel.TokenRequestBodies)
        {
            foreach (var (name, value) in QueryHelpers.ParseQuery("?" + body))
            {
                if (name is "code" or "code_verifier" or "client_assertion" or "refresh_token")
                {
                    secrets.Add(value.ToString());
                }
            }
        }

        var bffEvents = bffSink.Rendered().ToList();
        var identityEvents = environment.Sink.Rendered().ToList();
        foreach (var expected in new[] { "LoginSucceeded", "SessionCreated", "RefreshRejected", "RefreshOutcomeUncertain", "Logout-all", "Session deleted" })
        {
            Assert.Contains(bffEvents, entry => entry.Contains(expected, StringComparison.Ordinal));
        }

        Assert.True(secrets.Count > 20, $"Only {secrets.Count} secrets were collected.");
        foreach (var entry in bffEvents.Concat(identityEvents))
        {
            var leaked = secrets.FirstOrDefault(secret => entry.Contains(secret, StringComparison.Ordinal));
            Assert.True(leaked is null, $"Secret starting '{leaked?[..6]}' (length {leaked?.Length}) in: {Regex.Replace(entry, "eyJ[A-Za-z0-9_.-]+", "<jwt>")}");
            Assert.False(JwtLike().IsMatch(entry) || SensitiveParameter().IsMatch(entry) || TokenParameter().IsMatch(entry), entry);
        }
    }

    [GeneratedRegex(@"token=[A-Za-z0-9_-]{20,}")]
    private static partial Regex TokenParameter();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")]
    private static partial Regex JwtLike();

    [GeneratedRegex(@"(code_verifier|client_assertion|refresh_token|id_token_hint|code)=[^&\s]{8,}")]
    private static partial Regex SensitiveParameter();
}
