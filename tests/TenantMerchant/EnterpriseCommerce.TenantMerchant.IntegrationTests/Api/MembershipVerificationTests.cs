using System.Net;
using System.Net.Http.Json;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// The internal membership verification API for other services (ADR-025 section 1, TENANT_MERCHANT_API_CONTRACT.md
/// section 3.3): the verdict matrix, its own policy (independent of provisioning), and input validation.
/// </summary>
public sealed class MembershipVerificationTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    private const string Scope = "svc.tenant-merchant.membership-verification";

    private static readonly TokenSpec Verifier = new()
    {
        Subject = TenantMerchantApiFixture.VerifierClient,
        ClientId = TenantMerchantApiFixture.VerifierClient,
        Scope = Scope,
    };

    public static TheoryData<string, TokenSpec> TokensRejectedByTheVerificationPolicy() => new()
    {
        { "the provisioning client with the provisioning scope", new TokenSpec() },
        { "the provisioning client with the verification scope", new TokenSpec { Scope = Scope } },
        { "the verifier with the provisioning scope", Verifier with { Scope = TestTokenIssuer.ProvisioningScope } },
        { "the verifier without a scope", Verifier with { Scope = null } },
        { "sub differs from client_id", Verifier with { Subject = "someone" } },
        { "principal type user", Verifier with { PrincipalType = "user" } },
        { "merchant user token", TokenSpec.User("owner-mv") },
        { "merchant user token carrying the scope", TokenSpec.User("owner-mv") with { Scope = Scope, Audiences = ["ec-merchant", "tenant-merchant-api"] } },
    };

    [Fact]
    public async Task ActiveMembers_AreVerifiedWithTheirRole()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync($"mv-active-{Suffix()}", "owner-mv-1");
        await fixture.SeedMemberAsync(tenantId, "admin-mv-1", MerchantRole.MerchantAdmin);
        await fixture.SeedMemberAsync(tenantId, "staff-mv-1", MerchantRole.MerchantStaff);

        Assert.Equal(new VerdictDto(true, "MerchantOwner"), await VerifyAsync(tenantId, "owner-mv-1"));
        Assert.Equal(new VerdictDto(true, "MerchantAdmin"), await VerifyAsync(tenantId, "admin-mv-1"));
        Assert.Equal(new VerdictDto(true, "MerchantStaff"), await VerifyAsync(tenantId, "staff-mv-1"));
    }

    [Fact]
    public async Task EveryOtherCase_IsTheSameNegativeVerdict()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync($"mv-neg-{Suffix()}", "owner-mv-2");
        var otherTenantId = await fixture.ProvisionTenantAsync($"mv-other-{Suffix()}", "owner-mv-3");
        await fixture.SeedMemberAsync(tenantId, "suspended-mv", MerchantRole.MerchantAdmin, MembershipStatus.Suspended);
        await fixture.SeedMemberAsync(tenantId, "removed-mv", MerchantRole.MerchantStaff, MembershipStatus.Removed);

        var negative = new VerdictDto(false, null);
        Assert.Equal(negative, await VerifyAsync(tenantId, "suspended-mv"));
        Assert.Equal(negative, await VerifyAsync(tenantId, "removed-mv"));
        Assert.Equal(negative, await VerifyAsync(tenantId, "stranger-mv"));
        Assert.Equal(negative, await VerifyAsync(tenantId, "owner-mv-3"));
        Assert.Equal(negative, await VerifyAsync(otherTenantId, "owner-mv-2"));
        Assert.Equal(negative, await VerifyAsync(Guid.CreateVersion7(), "owner-mv-2"));
    }

    [Fact]
    public async Task TheVerdict_IsNeverCached()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync($"mv-live-{Suffix()}", "owner-mv-4");
        await fixture.SeedMemberAsync(tenantId, "staff-mv-4", MerchantRole.MerchantStaff);
        Assert.Equal(new VerdictDto(true, "MerchantStaff"), await VerifyAsync(tenantId, "staff-mv-4"));

        var membershipId = await fixture.MembershipIdAsync(tenantId, "staff-mv-4");
        using (var suspend = await fixture.SendAsUserAsync("owner-mv-4", HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships/{membershipId}/suspend"))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        }

        Assert.Equal(new VerdictDto(false, null), await VerifyAsync(tenantId, "staff-mv-4"));
    }

    [Fact]
    public async Task TheAnswer_IsNotStoredByCaches()
    {
        fixture.RequireAvailable();
        using var response = await SendAsync(fixture.Tokens.CreateToken(Verifier), Guid.CreateVersion7(), new { subjectId = "anyone" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [MemberData(nameof(TokensRejectedByTheVerificationPolicy))]
    public async Task OtherCallers_Are403(string reason, TokenSpec spec)
    {
        fixture.RequireAvailable();
        using var response = await SendAsync(fixture.Tokens.CreateToken(spec), Guid.CreateVersion7(), new { subjectId = "anyone" });

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{reason}: {response.StatusCode}");
    }

    [Fact]
    public async Task TheVerifier_CannotProvision()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(Verifier));
        using var response = await TenantMerchantApiFixture.ProvisionAsync(
            client, new { slug = $"mv-prov-{Suffix()}", displayName = "x", ownerSubjectId = "owner-x", actorReference = "a" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingOrForeignTokens_Are401()
    {
        fixture.RequireAvailable();

        using var anonymous = await SendAsync(null, Guid.CreateVersion7(), new { subjectId = "anyone" });
        using var storefront = await SendAsync(
            fixture.Tokens.CreateToken(Verifier with { Audiences = ["identity-api"] }), Guid.CreateVersion7(), new { subjectId = "anyone" });

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, storefront.StatusCode);
    }

    [Fact]
    public async Task AnUnknownField_Is400()
    {
        fixture.RequireAvailable();
        using var response = await SendAsync(
            fixture.Tokens.CreateToken(Verifier), Guid.CreateVersion7(), new { subjectId = "anyone", role = "MerchantOwner" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    public async Task AMalformedSubject_Is400(string? subjectId)
    {
        fixture.RequireAvailable();
        using var response = await SendAsync(fixture.Tokens.CreateToken(Verifier), Guid.CreateVersion7(), new { subjectId });

        using var problem = await ProvisioningApiTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("subjectId", out _));
    }

    private async Task<VerdictDto> VerifyAsync(Guid tenantId, string subjectId)
    {
        using var response = await SendAsync(fixture.Tokens.CreateToken(Verifier), tenantId, new { subjectId });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<VerdictDto>(TestContext.Current.CancellationToken))!;
    }

    private async Task<HttpResponseMessage> SendAsync(string? token, Guid tenantId, object body)
    {
        using var client = fixture.CreateClient(token);
        return await client.PostAsJsonAsync(
            $"/internal/v1/tenants/{tenantId}/membership-verifications", body, TestContext.Current.CancellationToken);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    private sealed record VerdictDto(bool Active, string? Role);
}
