using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Bff.IntegrationTests.MerchantFlow;

/// <summary>
/// Self-service merchant onboarding through the real path (PHASE_2_2 section 5.1.2, Phase 3 S3): browser → merchant
/// BFF → Gateway → Tenant &amp; Merchant, with Identity issuing the tokens. A registered user creates their own tenant
/// and becomes its owner; the browser never holds an access token and cannot choose the owner.
/// </summary>
public sealed class MerchantOnboardingFlowTests(MerchantFlowEnvironment environment) : IClassFixture<MerchantFlowEnvironment>
{
    [Fact]
    public async Task ASignedInUser_CreatesATenant_BecomesItsOwner_AndAdministersIt_ThroughTheBffOnly()
    {
        environment.RequireAvailable();
        var bff = environment.MerchantBff!;
        var email = await environment.CreateUserAsync();
        var subject = await environment.UserIdAsync(email);
        var slug = $"e2e-onboard-{Guid.NewGuid():N}"[..30];
        var key = Guid.NewGuid().ToString("D");

        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, email);
        var (_, xsrf) = await BffTestEnvironment.UserAsync(browser, bff);

        // A state-changing call: without the antiforgery token the BFF forwards nothing, and nothing is created.
        var forged = await CreateTenantAsync(browser, bff, xsrfToken: null, key, slug);
        Assert.Equal(HttpStatusCode.Forbidden, forged.Status);
        var none = await browser.FetchAsync(HttpMethod.Get, bff.Url("/api/v1/tenants"), xsrfToken: null);
        Assert.DoesNotContain(slug, none.Body, StringComparison.Ordinal);

        var created = await CreateTenantAsync(browser, bff, xsrf, key, slug);
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Guid tenantId;
        using (var body = JsonDocument.Parse(created.Body))
        {
            tenantId = body.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(slug, body.RootElement.GetProperty("slug").GetString());
            Assert.Equal(subject, body.RootElement.GetProperty("membership").GetProperty("subjectId").GetString());
            Assert.Equal("MerchantOwner", body.RootElement.GetProperty("membership").GetProperty("role").GetString());
        }

        // A retry of the same request (a lost response) is answered from the stored tenant.
        var replay = await CreateTenantAsync(browser, bff, xsrf, key, slug);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        Assert.Contains(tenantId.ToString(), replay.Body, StringComparison.OrdinalIgnoreCase);

        // The default limit is one self-service tenant per user.
        var second = await CreateTenantAsync(browser, bff, xsrf, Guid.NewGuid().ToString("D"), slug + "-2");
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Contains("tenant-limit-reached", second.Body, StringComparison.Ordinal);

        // The new owner sees the tenant and can invite staff to it.
        var mine = await browser.FetchAsync(HttpMethod.Get, bff.Url("/api/v1/tenants"), xsrfToken: null);
        Assert.Contains(tenantId.ToString(), mine.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MerchantOwner", mine.Body, StringComparison.Ordinal);
        var invited = await browser.FetchAsync(
            HttpMethod.Post,
            bff.Url($"/api/v1/tenants/{tenantId}/invitations"),
            xsrf,
            content: JsonContent.Create(new { email = $"staff-{Guid.NewGuid():N}@example.test", role = "MerchantStaff" }));
        Assert.Equal(HttpStatusCode.Created, invited.Status);

        foreach (var response in new[] { created, replay, second, mine })
        {
            Assert.DoesNotContain("eyJ", response.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BrowserSuppliedCredentialsAndOwnerFields_ChangeNothing()
    {
        environment.RequireAvailable();
        var bff = environment.MerchantBff!;
        var email = await environment.CreateUserAsync();
        var subject = await environment.UserIdAsync(email);
        var slug = $"e2e-forged-{Guid.NewGuid():N}"[..28];

        using var browser = environment.CreateBrowser(bff);
        await environment.SignInAsync(browser, bff, email);
        var (_, xsrf) = await BffTestEnvironment.UserAsync(browser, bff);

        // An owner field outside the contract is refused by Tenant & Merchant, whatever the browser claims.
        var smuggled = await CreateTenantAsync(
            browser, bff, xsrf, Guid.NewGuid().ToString("D"), slug, extraBody: new Dictionary<string, object?> { ["ownerSubjectId"] = "attacker" });
        Assert.Equal(HttpStatusCode.BadRequest, smuggled.Status);

        // A forged bearer token and forged role and tenant headers: the BFF uses the session's token, and the owner is
        // the session's user.
        var created = await CreateTenantAsync(
            browser,
            bff,
            xsrf,
            Guid.NewGuid().ToString("D"),
            slug,
            configure: request =>
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "forged.browser.token");
                request.Headers.Add("X-Merchant-Role", "MerchantStaff");
                request.Headers.Add("X-Owner-Subject", "attacker");
            });
        Assert.Equal(HttpStatusCode.Created, created.Status);
        using var body = JsonDocument.Parse(created.Body);
        Assert.Equal(subject, body.RootElement.GetProperty("membership").GetProperty("subjectId").GetString());
    }

    [Fact]
    public async Task WithoutASession_NothingIsForwarded()
    {
        environment.RequireAvailable();
        var bff = environment.MerchantBff!;
        using var anonymous = environment.CreateBrowser(bff);

        var response = await CreateTenantAsync(anonymous, bff, xsrfToken: null, Guid.NewGuid().ToString("D"), "e2e-anonymous");

        Assert.Contains(response.Status, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }

    private static Task<BrowserResponse> CreateTenantAsync(
        TestBrowser browser,
        BffHost bff,
        string? xsrfToken,
        string idempotencyKey,
        string slug,
        IDictionary<string, object?>? extraBody = null,
        Action<HttpRequestMessage>? configure = null)
    {
        var body = new Dictionary<string, object?> { ["slug"] = slug, ["displayName"] = $"{slug} store" };
        foreach (var (name, value) in extraBody ?? new Dictionary<string, object?>())
        {
            body[name] = value;
        }

        var url = bff.Url("/api/v1/tenants");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", url.GetLeftPart(UriPartial.Authority));
        if (xsrfToken is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", xsrfToken);
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        configure?.Invoke(request);
        return browser.SendAsync(request);
    }
}
