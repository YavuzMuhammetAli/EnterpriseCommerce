using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Bff.IntegrationTests.MerchantFlow;

/// <summary>
/// The merchant membership flow through the real path (ADR-016, ADR-021): browser → merchant BFF → Gateway →
/// Tenant &amp; Merchant, with Identity issuing the tokens and verifying the invitee's email. The browser never holds an
/// access token, and Tenant &amp; Merchant authorizes every request from its own data, whatever the browser sends.
/// </summary>
public sealed class MerchantMembershipFlowTests(MerchantFlowEnvironment environment) : IClassFixture<MerchantFlowEnvironment>
{
    [Fact]
    public async Task AnOwnerInvites_TheInvitedUserAccepts_AndBothSeeTheTenant_ThroughTheBffOnly()
    {
        environment.RequireAvailable();
        var bff = environment.MerchantBff!;
        var ownerEmail = await environment.CreateUserAsync();
        var inviteeEmail = await environment.CreateUserAsync();
        var ownerSubject = await environment.UserIdAsync(ownerEmail);
        var inviteeSubject = await environment.UserIdAsync(inviteeEmail);
        var tenantId = await environment.ProvisionTenantAsync("e2e-flow", ownerSubject);

        using var owner = environment.CreateBrowser(bff);
        await environment.SignInAsync(owner, bff, ownerEmail);
        var (_, ownerXsrf) = await BffTestEnvironment.UserAsync(owner, bff);

        // The owner sees the tenant: a user token of ec-merchant, validated by Tenant & Merchant against Identity.
        var mine = await owner.FetchAsync(HttpMethod.Get, bff.Url("/api/v1/tenants"), xsrfToken: null);
        Assert.Equal(HttpStatusCode.OK, mine.Status);
        Assert.Contains(tenantId.ToString(), mine.Body, StringComparison.OrdinalIgnoreCase);

        // Creating an invitation is a state-changing call: without the antiforgery token the BFF forwards nothing.
        var forged = await owner.FetchAsync(HttpMethod.Post, bff.Url($"/api/v1/tenants/{tenantId}/invitations"), xsrfToken: null, content: Json(new { email = inviteeEmail, role = "MerchantStaff" }));
        Assert.Equal(HttpStatusCode.Forbidden, forged.Status);

        var created = await owner.FetchAsync(HttpMethod.Post, bff.Url($"/api/v1/tenants/{tenantId}/invitations"), ownerXsrf, content: Json(new { email = inviteeEmail, role = "MerchantStaff" }));
        Assert.Equal(HttpStatusCode.Created, created.Status);
        Assert.Equal("no-store", created.Header("Cache-Control"));
        using var invitation = JsonDocument.Parse(created.Body);
        var token = invitation.RootElement.GetProperty("acceptanceToken").GetString()!;
        Assert.StartsWith("ecmi_", token, StringComparison.Ordinal);

        // The invitee signs in to the merchant portal and accepts. Identity confirms the verified email to Tenant & Merchant.
        using var invitee = environment.CreateBrowser(bff);
        await environment.SignInAsync(invitee, bff, inviteeEmail);
        var (_, inviteeXsrf) = await BffTestEnvironment.UserAsync(invitee, bff);
        var accepted = await invitee.FetchAsync(HttpMethod.Post, bff.Url("/api/v1/merchant-invitations/accept"), inviteeXsrf, content: Json(new { token }));
        Assert.Equal(HttpStatusCode.Created, accepted.Status);
        using (var body = JsonDocument.Parse(accepted.Body))
        {
            Assert.Equal(tenantId, body.RootElement.GetProperty("tenantId").GetGuid());
            Assert.Equal(inviteeSubject, body.RootElement.GetProperty("membership").GetProperty("subjectId").GetString());
            Assert.Equal("MerchantStaff", body.RootElement.GetProperty("membership").GetProperty("role").GetString());
        }

        var inviteeTenants = await invitee.FetchAsync(HttpMethod.Get, bff.Url("/api/v1/tenants"), xsrfToken: null);
        Assert.Contains(tenantId.ToString(), inviteeTenants.Body, StringComparison.OrdinalIgnoreCase);

        // No access token ever reached either browser.
        foreach (var response in new[] { mine, created, accepted, inviteeTenants })
        {
            Assert.DoesNotContain("eyJ", response.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AUserWhoseVerifiedEmailDiffers_CannotUseSomeoneElsesInvitation()
    {
        environment.RequireAvailable();
        var bff = environment.MerchantBff!;
        var ownerEmail = await environment.CreateUserAsync();
        var invitedEmail = await environment.CreateUserAsync();
        var strangerEmail = await environment.CreateUserAsync();
        var tenantId = await environment.ProvisionTenantAsync("e2e-mismatch", await environment.UserIdAsync(ownerEmail));

        using var owner = environment.CreateBrowser(bff);
        await environment.SignInAsync(owner, bff, ownerEmail);
        var (_, ownerXsrf) = await BffTestEnvironment.UserAsync(owner, bff);
        var created = await owner.FetchAsync(HttpMethod.Post, bff.Url($"/api/v1/tenants/{tenantId}/invitations"), ownerXsrf, content: Json(new { email = invitedEmail, role = "MerchantAdmin" }));
        using var invitation = JsonDocument.Parse(created.Body);
        var token = invitation.RootElement.GetProperty("acceptanceToken").GetString()!;

        // The stranger holds the token (a forwarded or intercepted link) but is verified for another address.
        using var stranger = environment.CreateBrowser(bff);
        await environment.SignInAsync(stranger, bff, strangerEmail);
        var (_, strangerXsrf) = await BffTestEnvironment.UserAsync(stranger, bff);
        var refused = await stranger.FetchAsync(HttpMethod.Post, bff.Url("/api/v1/merchant-invitations/accept"), strangerXsrf, content: Json(new { token }));

        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("invitation-email-mismatch", refused.Body, StringComparison.Ordinal);
        var strangerTenants = await stranger.FetchAsync(HttpMethod.Get, bff.Url("/api/v1/tenants"), xsrfToken: null);
        Assert.DoesNotContain(tenantId.ToString(), strangerTenants.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BrowserSuppliedIdentityRoleOrTenantValues_ChangeNothing_TheServiceAuthorizesItself()
    {
        environment.RequireAvailable();
        var bff = environment.MerchantBff!;
        var ownerEmail = await environment.CreateUserAsync();
        var staffEmail = await environment.CreateUserAsync();
        var ownerSubject = await environment.UserIdAsync(ownerEmail);
        var tenantId = await environment.ProvisionTenantAsync("e2e-forged", ownerSubject);

        // The staff member joins through an invitation.
        using var owner = environment.CreateBrowser(bff);
        await environment.SignInAsync(owner, bff, ownerEmail);
        var (_, ownerXsrf) = await BffTestEnvironment.UserAsync(owner, bff);
        var created = await owner.FetchAsync(HttpMethod.Post, bff.Url($"/api/v1/tenants/{tenantId}/invitations"), ownerXsrf, content: Json(new { email = staffEmail, role = "MerchantStaff" }));
        using var invitation = JsonDocument.Parse(created.Body);
        using var staff = environment.CreateBrowser(bff);
        await environment.SignInAsync(staff, bff, staffEmail);
        var (_, staffXsrf) = await BffTestEnvironment.UserAsync(staff, bff);
        var accepted = await staff.FetchAsync(HttpMethod.Post, bff.Url("/api/v1/merchant-invitations/accept"), staffXsrf, content: Json(new { token = invitation.RootElement.GetProperty("acceptanceToken").GetString() }));
        Assert.Equal(HttpStatusCode.Created, accepted.Status);

        var members = await owner.FetchAsync(HttpMethod.Get, bff.Url($"/api/v1/tenants/{tenantId}/memberships"), xsrfToken: null);
        using var page = JsonDocument.Parse(members.Body);
        var ownerMembership = page.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("subjectId").GetString() == ownerSubject).GetProperty("id").GetGuid();

        // Staff try to suspend the owner, with a forged bearer token and forged role and tenant headers.
        using var request = new HttpRequestMessage(HttpMethod.Post, bff.Url($"/api/v1/tenants/{tenantId}/memberships/{ownerMembership}/suspend"));
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Origin", bff.Origin.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-XSRF-TOKEN", staffXsrf);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "forged.browser.token");
        request.Headers.Add("X-Merchant-Role", "MerchantOwner");
        request.Headers.Add("X-Tenant-Id", tenantId.ToString());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var attempt = await staff.SendAsync(request);

        // The BFF replaced the bearer with the session's token (so: not 401), and the service refused by role.
        Assert.Equal(HttpStatusCode.Forbidden, attempt.Status);
        Assert.Contains("permission-denied", attempt.Body, StringComparison.Ordinal);

        // The owner, through the same path, suspends the staff member, who then sees no tenant at all.
        var staffMembership = JsonDocument.Parse(accepted.Body).RootElement.GetProperty("membership").GetProperty("id").GetGuid();
        var suspended = await owner.FetchAsync(HttpMethod.Post, bff.Url($"/api/v1/tenants/{tenantId}/memberships/{staffMembership}/suspend"), ownerXsrf);
        Assert.Equal(HttpStatusCode.OK, suspended.Status);
        var afterSuspension = await staff.FetchAsync(HttpMethod.Get, bff.Url($"/api/v1/tenants/{tenantId}"), xsrfToken: null);
        Assert.Equal(HttpStatusCode.NotFound, afterSuspension.Status);
    }

    [Theory]
    [InlineData("/internal/v1/tenants")]
    [InlineData("/internal/v1/accounts/someone/email-verification")]
    [InlineData("/api/v1/merchant-invitations/../../../internal/v1/tenants")]
    public async Task TheGateway_ForwardsNoInternalPath(string path)
    {
        environment.RequireAvailable();
        using var client = environment.Gateway.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { BaseAddress = MerchantFlowEnvironment.GatewayAddress });

        using var response = await client.PostAsJsonAsync(path, new { }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static JsonContent Json(object body) => JsonContent.Create(body);
}
