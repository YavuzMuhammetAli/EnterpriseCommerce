using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Security;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.AspNetCore.Http;

namespace EnterpriseCommerce.Bff.UnitTests;

public sealed class ReturnUrlValidatorTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/orders")]
    [InlineData("/orders/42?tab=items#top")]
    [InlineData("/search?q=a%20b")]
    public void LocalPaths_AreKept(string url) => Assert.Equal(url, ReturnUrlValidator.Normalize(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://evil.example/")]
    [InlineData("http://evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/%2fevil.example")]
    [InlineData("/%2Fevil.example")]
    [InlineData("/%5cevil.example")]
    [InlineData("/ /evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/%09/evil.example")]
    [InlineData("/x\r\nSet-Cookie: a=b")]
    [InlineData("javascript:alert(1)")]
    [InlineData("orders")]
    [InlineData("/bff/logout")]
    [InlineData("/bff/callback?code=x")]
    public void AnythingElse_FallsBackToTheRoot(string? url) => Assert.Equal("/", ReturnUrlValidator.Normalize(url));

    [Fact]
    public void OverlongUrls_AreRejected() => Assert.Equal("/", ReturnUrlValidator.Normalize("/" + new string('a', 3000)));
}

public sealed class BrowserRequestProtectionTests
{
    private const string Origin = "https://storefront.example.test";

    [Theory]
    [InlineData("same-origin", null, true)]
    [InlineData("same-origin", Origin, true)]
    [InlineData("same-origin", "https://merchant.example.test", false)]
    [InlineData("same-site", Origin, false)]
    [InlineData("cross-site", Origin, false)]
    [InlineData("none", null, false)]
    [InlineData(null, Origin, true)]
    [InlineData(null, "https://STOREFRONT.example.test", true)]
    [InlineData(null, "https://storefront.example.test:8443", false)]
    [InlineData(null, "http://storefront.example.test", false)]
    [InlineData(null, "null", false)]
    [InlineData(null, null, false)]
    public void FetchMetadataAndOrigin_Matrix(string? fetchSite, string? origin, bool accepted)
    {
        var context = new DefaultHttpContext();
        if (fetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        Assert.Equal(accepted, BrowserRequestProtection.IsSameOrigin(context.Request, Origin));
    }

    [Theory]
    [InlineData("POST", "/bff/logout", true)]
    [InlineData("POST", "/bff/logout-all", true)]
    [InlineData("DELETE", "/api/orders/1", true)]
    [InlineData("PUT", "/API/orders/1", true)]
    [InlineData("PATCH", "/api/orders/1", true)]
    [InlineData("GET", "/bff/user", false)]
    [InlineData("HEAD", "/api/orders", false)]
    [InlineData("POST", "/static/form", false)]
    public void StateChangingRequestsToBffAndApi_AreProtected(string method, string path, bool isProtected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;

        Assert.Equal(isProtected, BrowserRequestProtection.IsProtected(context.Request));
    }
}

public sealed class SessionIdsTests
{
    [Fact]
    public void Keys_Are256BitRandomAndWellFormed()
    {
        var keys = Enumerable.Range(0, 1000).Select(_ => SessionIds.NewKey()).ToList();

        Assert.All(keys, key => Assert.True(SessionIds.IsWellFormedKey(key)));
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RecordIdAndLogReference_NeverContainTheKey()
    {
        var key = SessionIds.NewKey();
        var recordId = SessionIds.RecordId(key);
        var reference = SessionIds.LogReference(recordId);

        Assert.DoesNotContain(key, recordId, StringComparison.Ordinal);
        Assert.Equal(64, recordId.Length);
        Assert.Equal(12, reference.Length);
        Assert.False(recordId.StartsWith(reference, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:b")]
    public void MalformedCookieValues_AreRejectedBeforeAnyLookup(string? value) =>
        Assert.False(SessionIds.IsWellFormedKey(value));
}

public sealed class SessionTokenTests
{
    private static readonly DateTimeOffset Issued = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, 7, false)]
    [InlineData(10, 8, true)]
    [InlineData(10, 9, true)]
    [InlineData(5, 3, false)]
    [InlineData(5, 4, true)]
    public void Refresh_StartsAtTheThresholdOrAThirdOfTheLifetime(int lifetimeMinutes, int elapsedMinutes, bool due)
    {
        var tokens = new SessionTokens("at", "rt", "id", Issued.AddMinutes(lifetimeMinutes), Issued);

        Assert.Equal(due, tokens.NeedsRefresh(Issued.AddMinutes(elapsedMinutes), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void Expiry_IsExact()
    {
        var tokens = new SessionTokens("at", null, null, Issued.AddMinutes(10), Issued);

        Assert.False(tokens.IsExpired(Issued.AddMinutes(10).AddTicks(-1)));
        Assert.True(tokens.IsExpired(Issued.AddMinutes(10)));
    }

    [Theory]
    [InlineData("noeviction", true)]
    [InlineData("allkeys-lru", false)]
    [InlineData("volatile-lru", false)]
    [InlineData("NOEVICTION", false)]
    [InlineData(null, false)]
    public void OnlyNoEviction_IsAccepted(string? policy, bool accepted) =>
        Assert.Equal(accepted, SessionStorePolicyCheck.IsAccepted(policy));
}

public sealed class BffSettingsValidationTests
{
    internal static BffSettings Valid() => new()
    {
        Deployment = "storefront",
        ClientId = "bff-storefront",
        Audience = "ec-storefront",
        Authority = "https://id.example.test/",
        PublicOrigin = "https://storefront.example.test",
        ClientKey = new BffClientKeySettings { PrivateKeyPath = "/run/secrets/bff.pem", KeyId = "k1" },
        DataProtectionKeyDirectory = "/var/bff/keys",
    };

    [Fact]
    public void ValidSettings_PassInEveryEnvironment()
    {
        Assert.Empty(BffSettingsValidation.Validate(Valid(), isDevelopment: false));
        Assert.Empty(BffSettingsValidation.Validate(Valid(), isDevelopment: true));
    }

    private static readonly Dictionary<string, Action<BffSettings>> InvalidChanges = new()
    {
        ["service client prefix"] = s => s.ClientId = "svc-orders",
        ["missing client"] = s => s.ClientId = "",
        ["missing audience"] = s => s.Audience = "",
        ["http authority"] = s => s.Authority = "http://id.example.test/",
        ["authority without slash"] = s => s.Authority = "https://id.example.test",
        ["http origin"] = s => s.PublicOrigin = "http://storefront.example.test",
        ["origin with path"] = s => s.PublicOrigin = "https://storefront.example.test/app",
        ["origin with slash"] = s => s.PublicOrigin = "https://storefront.example.test/",
        ["upper-case deployment"] = s => s.Deployment = "Storefront",
        ["no key"] = s => s.ClientKey = new BffClientKeySettings { KeyId = "k1" },
        ["two keys"] = s => s.ClientKey.PrivateKeyPem = "pem",
        ["no key id"] = s => s.ClientKey.KeyId = "",
        ["idle longer than absolute"] = s => s.Session.IdleTimeout = TimeSpan.FromDays(2),
        ["lease shorter than request"] = s => s.Refresh.LeaseDuration = TimeSpan.FromSeconds(1),
        ["external post-logout landing"] = s => s.PostLogoutLandingPath = "//evil.example",
        ["no key ring outside development"] = s => s.DataProtectionKeyDirectory = null,
    };

    public static TheoryData<string> Invalid => [.. InvalidChanges.Keys];

    [Theory]
    [MemberData(nameof(Invalid))]
    public void InvalidSettings_AreRejected(string reason)
    {
        var settings = Valid();
        InvalidChanges[reason](settings);

        Assert.True(BffSettingsValidation.Validate(settings, isDevelopment: false).Any(), reason);
    }

    [Fact]
    public void InlinePem_IsDevelopmentOnly()
    {
        var settings = Valid();
        settings.ClientKey = new BffClientKeySettings { PrivateKeyPem = "pem", KeyId = "k1" };

        Assert.Empty(BffSettingsValidation.Validate(settings, isDevelopment: true));
        Assert.NotEmpty(BffSettingsValidation.Validate(settings, isDevelopment: false));
    }

    [Fact]
    public void CookieNames_AreHostPrefixedAndDistinctPerDeployment()
    {
        var storefront = BffCookieNames.For(Valid());
        var merchantSettings = Valid();
        merchantSettings.Deployment = "merchant";
        var merchant = BffCookieNames.For(merchantSettings);

        Assert.Equal("__Host-Http-ec-storefront", storefront.Session);
        Assert.All(new[] { storefront.Correlation, storefront.Nonce, storefront.Antiforgery, storefront.RequestToken }, name => Assert.StartsWith("__Host-", name, StringComparison.Ordinal));
        Assert.NotEqual(storefront.Session, merchant.Session);
        Assert.NotEqual(storefront.Correlation, merchant.Correlation);

        merchantSettings.Cookies.UseHttpOnlyPrefix = false;
        Assert.Equal("__Host-ec-merchant", BffCookieNames.For(merchantSettings).Session);
    }
}
