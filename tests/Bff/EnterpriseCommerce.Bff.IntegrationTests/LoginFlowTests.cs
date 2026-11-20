using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.WebUtilities;

namespace EnterpriseCommerce.Bff.IntegrationTests;

/// <summary>Authorization code + PKCE S256 + private_key_jwt through the real Identity host (ADR-016 section 2).</summary>
public sealed class LoginFlowTests(BffTestEnvironment environment) : IClassFixture<BffTestEnvironment>
{
    [Fact]
    public async Task Login_CreatesAServerSideSession_AndTheBrowserNeverSeesAToken()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();

        var landing = await environment.SignInAsync(browser, bff, email, returnUrl: "/orders?tab=open");

        // The browser lands on the validated local return URL of the BFF.
        Assert.Equal(bff.Url("/orders?tab=open"), landing.Url);

        // The authorization request: code flow, PKCE S256, registered redirect URI, state and nonce, no tokens.
        var authorize = browser.Transcript.First(r => r.Url.AbsolutePath == "/connect/authorize").Url;
        var query = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("query", query["response_mode"]);
        Assert.Equal(bff.Url("/bff/callback").AbsoluteUri, query["redirect_uri"]);
        Assert.Equal(environment.Storefront.ClientId, query["client_id"]);
        Assert.Contains("offline_access", query["scope"].ToString(), StringComparison.Ordinal);
        Assert.True(query["state"].ToString().Length > 20);
        Assert.True(query["nonce"].ToString().Length > 20);

        // The code was exchanged with private_key_jwt and the PKCE verifier, never a client secret.
        var exchange = Assert.Single(bff.Channel.TokenRequestBodies);
        Assert.Contains("grant_type=authorization_code", exchange, StringComparison.Ordinal);
        Assert.Contains("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer", exchange, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", exchange, StringComparison.Ordinal);
        Assert.DoesNotContain("client_secret", exchange, StringComparison.Ordinal);

        // The session exists server-side with all three tokens ...
        var tokens = await bff.TokensOfAsync(browser);
        Assert.NotNull(tokens);
        Assert.NotNull(tokens.Tokens.RefreshToken);
        Assert.NotNull(tokens.Tokens.IdToken);

        // ... and no BFF response, header or cookie ever carried one of them.
        var secrets = new[] { tokens.Tokens.AccessToken, tokens.Tokens.RefreshToken!, tokens.Tokens.IdToken! };
        foreach (var response in browser.Transcript)
        {
            var everything = response.Body + string.Join("\n", response.Headers.SelectMany(h => h.Value.Select(v => h.Key + ": " + v)));
            Assert.All(secrets, secret => Assert.DoesNotContain(secret, everything, StringComparison.Ordinal));
            if (response.Url.Host == bff.Origin.Host)
            {
                Assert.DoesNotContain("eyJ", everything, StringComparison.Ordinal);
            }
        }

        // /bff/user reports the user, not the tokens.
        var (user, xsrf) = await BffTestEnvironment.UserAsync(browser, bff);
        Assert.Equal(HttpStatusCode.OK, user.Status);
        using var body = JsonDocument.Parse(user.Body);
        Assert.Equal(await environment.UserIdAsync(email), body.RootElement.GetProperty("sub").GetString());
        Assert.False(body.RootElement.TryGetProperty("access_token", out _));
        Assert.NotNull(xsrf);
        Assert.Equal("no-store", user.Header("Cache-Control"));
    }

    [Fact]
    public async Task SessionAndCorrelationCookies_HaveTheAcceptedAttributes()
    {
        environment.RequireAvailable();
        var bff = environment.CreateBff(environment.Storefront);
        using var browser = environment.CreateBrowser(bff);
        var email = await environment.CreateUserAsync();

        await environment.SignInAsync(browser, bff, email);

        var login = browser.Transcript.First(r => r.Url.AbsolutePath == "/bff/login");
        var correlation = login.SetCookies.Where(c => c.StartsWith("__Host-ec-storefront-oidc-", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, correlation.Count);
        Assert.All(correlation, cookie =>
        {
            var attributes = cookie.ToLowerInvariant();
            Assert.Contains("; secure", attributes, StringComparison.Ordinal);
            Assert.Contains("; httponly", attributes, StringComparison.Ordinal);
            Assert.Contains("; samesite=lax", attributes, StringComparison.Ordinal);
            Assert.Contains("; path=/", attributes, StringComparison.Ordinal);
            Assert.DoesNotContain("domain=", attributes, StringComparison.Ordinal);
        });

        var callback = browser.Transcript.First(r => r.Url.AbsolutePath == "/bff/callback");
        var session = callback.SetCookie("__Host-Http-ec-storefront");
        Assert.NotNull(session);
        var sessionAttributes = session.ToLowerInvariant();
        Assert.Contains("; secure", sessionAttributes, StringComparison.Ordinal);
        Assert.Contains("; httponly", sessionAttributes, StringComparison.Ordinal);
        Assert.Contains("; samesite=strict", sessionAttributes, StringComparison.Ordinal);
        Assert.Contains("; path=/", sessionAttributes, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", sessionAttributes, StringComparison.Ordinal);
        Assert.DoesNotContain("expires=", sessionAttributes, StringComparison.Ordinal);

        // The value is an opaque 256-bit key, not a ticket.
        var value = session.Split(';')[0].Split('=', 2)[1];
        Assert.Equal(43, value.Length);

        // The correlation cookies are deleted at the callback.
        Assert.All(callback.SetCookies.Where(c => c.StartsWith("__Host-ec-storefront-oidc-", StringComparison.Ordinal)),
            cookie => Assert.Contains("expires=thu, 01 jan 1970", cookie.ToLowerInvariant(), StringComparison.Ordinal));
    }
}
