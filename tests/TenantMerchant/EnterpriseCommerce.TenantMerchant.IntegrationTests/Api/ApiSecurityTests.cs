using System.Net;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// Fail-closed authentication and caller separation (ADR-017 sections 3 and 4, PHASE_2_2_AUTHENTICATION_FLOWS.md
/// section 5.7). <c>401</c>: no token that is valid for this service. <c>403</c>: a valid token of the wrong caller.
/// </summary>
public sealed class ApiSecurityTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    public static TheoryData<string, TokenSpec> TokensRejectedAtAuthentication() => new()
    {
        { "wrong signing key", new TokenSpec { SignWithUnknownKey = true } },
        { "wrong issuer", new TokenSpec { Issuer = "https://evil.test/" } },
        { "expired", new TokenSpec { ExpiresIn = TimeSpan.FromMinutes(-5) } },
        { "typ JWT instead of at+jwt", new TokenSpec { TokenType = "JWT" } },
        { "RS384 instead of RS256", new TokenSpec { Algorithm = SecurityAlgorithms.RsaSha384 } },
        { "another service's audience", new TokenSpec { Audiences = ["identity-api"], Scope = "svc.identity.account-lifecycle" } },
        { "another front end's audience", TokenSpec.User("owner-sec") with { Audiences = ["ec-storefront"] } },
    };

    public static TheoryData<string, TokenSpec> TokensRejectedByTheProvisioningPolicy() => new()
    {
        { "right audience, wrong scope", new TokenSpec { Scope = "svc.tenant-merchant.reporting" } },
        { "right audience, no scope", new TokenSpec { Scope = null } },
        { "client not allow-listed", new TokenSpec { Subject = "svc-test-rogue", ClientId = "svc-test-rogue" } },
        { "sub differs from client_id", new TokenSpec { Subject = "someone" } },
        { "not a svc- client", new TokenSpec { Subject = "test-admin", ClientId = "test-admin" } },
        { "principal type user", new TokenSpec { PrincipalType = "user" } },
        { "no principal type", new TokenSpec { PrincipalType = null } },
        { "merchant user token", TokenSpec.User("owner-sec") },
        { "user token carrying the provisioning scope", TokenSpec.User("owner-sec") with { Scope = "svc.tenant-merchant.provisioning", Audiences = ["ec-merchant", "tenant-merchant-api"] } },
    };

    [Theory]
    [InlineData("POST", "/internal/v1/tenants")]
    [InlineData("GET", "/internal/v1/tenants/0196f0a0-0000-7000-8000-000000000001")]
    [InlineData("GET", "/api/v1/tenants/0196f0a0-0000-7000-8000-000000000001")]
    [InlineData("GET", "/api/v1/tenants/0196f0a0-0000-7000-8000-000000000001/memberships")]
    public async Task MissingOrGarbageTokens_Are401(string method, string path)
    {
        fixture.RequireAvailable();

        foreach (var token in new string?[] { null, "not-a-token", "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0." })
        {
            using var client = fixture.CreateClient(token);
            using var response = await client.SendAsync(Request(method, path), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [MemberData(nameof(TokensRejectedAtAuthentication))]
    public async Task InvalidTokens_Are401OnEveryEndpoint(string reason, TokenSpec spec)
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(spec));

        using var provision = await TenantMerchantApiFixture.ProvisionAsync(client, ProvisioningApiTests.Body($"rejected-{Math.Abs(reason.GetHashCode(StringComparison.Ordinal))}"));
        using var read = await client.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, provision.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, read.StatusCode);
    }

    [Theory]
    [MemberData(nameof(TokensRejectedByTheProvisioningPolicy))]
    public async Task WrongCallers_Are403OnTheInternalApi(string reason, TokenSpec spec)
    {
        fixture.RequireAvailable();
        var slug = $"denied-{Math.Abs(reason.GetHashCode(StringComparison.Ordinal))}";
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(spec));

        using var provision = await TenantMerchantApiFixture.ProvisionAsync(client, ProvisioningApiTests.Body(slug));
        using var read = await client.GetAsync($"/internal/v1/tenants/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, provision.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Tenants WHERE Slug = '{slug}'"));
    }

    [Fact]
    public async Task SelfProvisioning_ByAUser_IsRefused()
    {
        // A merchant-portal user tries to create a tenant naming themselves the owner.
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.UserToken("self-promoter"));

        using var response = await TenantMerchantApiFixture.ProvisionAsync(client, ProvisioningApiTests.Body("self-made", owner: "self-promoter"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Memberships WHERE SubjectId = 'self-promoter'"));
    }

    [Fact]
    public async Task ServiceTokens_AreRefusedOnTheMemberApi()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("service-on-user-api", "owner-svc");
        using var client = fixture.CreateClient(fixture.AdminToken());

        using var tenant = await client.GetAsync($"/api/v1/tenants/{tenantId}", TestContext.Current.CancellationToken);
        using var memberships = await client.GetAsync($"/api/v1/tenants/{tenantId}/memberships", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, tenant.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, memberships.StatusCode);
    }

    [Fact]
    public async Task AUserTokenOfASvcClient_IsRefusedOnTheMemberApi()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("svc-user-token", "owner-svcuser");
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(TokenSpec.User("owner-svcuser") with { ClientId = "svc-test-admin" }));

        using var response = await client.GetAsync($"/api/v1/tenants/{tenantId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnEmptyAllowList_DeniesEveryServiceCaller()
    {
        fixture.RequireAvailable();
        await using var factory = fixture.CreateFactory(fixture.Database.ConnectionString, allowedClients: []);
        using var client = TenantMerchantApiFixture.CreateClient(factory, fixture.AdminToken());

        using var response = await TenantMerchantApiFixture.ProvisionAsync(client, ProvisioningApiTests.Body("nobody-allowed"));
        using var read = await client.GetAsync($"/internal/v1/tenants/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'nobody-allowed'"));
    }

    [Fact]
    public async Task ANonServiceClientInTheAllowList_StopsTheHost()
    {
        fixture.RequireAvailable();
        await using var factory = fixture.CreateFactory(fixture.Database.ConnectionString, allowedClients: ["bff-admin"]);

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("AllowedClientIds", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForgedRoleAndTenantClaims_GrantNothing()
    {
        // Tokens never carry roles or tenants (ADR-017 section 2); a forged one must not change any decision.
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("forged-claims", "owner-forged");
        var forged = TokenSpec.User("outsider") with
        {
            ExtraClaims = new Dictionary<string, object>
            {
                ["role"] = "MerchantOwner",
                ["roles"] = new[] { "MerchantOwner" },
                ["tenant_id"] = tenantId.ToString(),
                ["tenant_context"] = tenantId.ToString(),
            },
        };
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(forged));

        using var response = await client.GetAsync($"/api/v1/tenants/{tenantId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AValidAllowListedServiceToken_IsAccepted()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(new TokenSpec { Scope = "openid svc.tenant-merchant.provisioning" }));

        using var response = await TenantMerchantApiFixture.ProvisionAsync(client, ProvisioningApiTests.Body("accepted-token"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static HttpRequestMessage Request(string method, string path) => method == "POST"
        ? new HttpRequestMessage(HttpMethod.Post, path) { Content = System.Net.Http.Json.JsonContent.Create(ProvisioningApiTests.Body("unauthenticated")) }
        : new HttpRequestMessage(HttpMethod.Get, path);
}
