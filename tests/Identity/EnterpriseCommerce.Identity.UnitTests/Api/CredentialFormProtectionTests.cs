using EnterpriseCommerce.Identity.Api.Accounts;
using Microsoft.AspNetCore.Http;

namespace EnterpriseCommerce.Identity.UnitTests.Api;

public sealed class CredentialFormProtectionTests
{
    [Theory]
    [InlineData("same-origin", null, true)]
    [InlineData("same-site", "https://id.example.test", false)] // a sibling subdomain is same-site, not same-origin
    [InlineData("cross-site", "https://id.example.test", false)]
    [InlineData("none", "https://id.example.test", false)]
    [InlineData(null, "https://id.example.test", true)] // no Fetch Metadata: exact Origin match
    [InlineData(null, "https://evil.example.test", false)]
    [InlineData(null, "https://id.example.test:8443", false)]
    [InlineData(null, "null", false)]
    [InlineData(null, null, false)] // neither signal: rejected
    public void IsSameOrigin_AppliesFetchMetadataThenExactOrigin(string? fetchSite, string? origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("id.example.test");
        if (fetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        Assert.Equal(expected, CredentialFormProtection.IsSameOrigin(context.Request));
    }

    [Theory]
    [InlineData("/account/login", true)]
    [InlineData("/account/reset-password", true)]
    [InlineData("/ACCOUNT/change-password", true)]
    [InlineData("/connect/endsession", true)] // its POST signs the browser out
    [InlineData("/connect/token", false)] // back-channel, client-authenticated
    [InlineData("/connect/revoke", false)]
    [InlineData("/api/v1/auth/logout-all", false)] // bearer only, never the cookie
    [InlineData("/accounts", false)]
    public void IsProtectedPath_CoversTheAccountPagesAndEndSession(string path, bool expected) =>
        Assert.Equal(expected, CredentialFormProtection.IsProtectedPath(new PathString(path)));
}
