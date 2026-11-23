using System.Net;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// Fail-closed merchant authorization (ADR-014 rule 4, ADR-017, ADR-025): token validation, caller separation, forged
/// claims, cross-tenant access and an unavailable membership authority.
/// </summary>
[Collection(nameof(MerchantSecurityCollection))]
public sealed class MerchantSecurityTests(CatalogMongoFixture fixture) : IClassFixture<CatalogMongoFixture>, IDisposable
{
    public static TheoryData<string, TokenSpec> TokensRejectedAtAuthentication() => new()
    {
        { "wrong signing key", TokenSpec.User("owner-x") with { SignWithUnknownKey = true } },
        { "wrong issuer", TokenSpec.User("owner-x") with { Issuer = "https://evil.test/" } },
        { "expired", TokenSpec.User("owner-x") with { ExpiresIn = TimeSpan.FromMinutes(-5) } },
        { "typ JWT instead of at+jwt", TokenSpec.User("owner-x") with { TokenType = "JWT" } },
        { "RS384 instead of RS256", TokenSpec.User("owner-x") with { Algorithm = SecurityAlgorithms.RsaSha384 } },
        { "the storefront audience", TokenSpec.User("owner-x") with { Audiences = ["ec-storefront"] } },
        { "another service's audience", new TokenSpec { Audiences = ["tenant-merchant-api"] } },
    };

    public static TheoryData<string, TokenSpec> TokensRejectedByTheMerchantPolicy() => new()
    {
        { "the taxonomy service token", new TokenSpec() },
        { "a user token of a svc- client", TokenSpec.User("owner-x") with { ClientId = "svc-rogue" } },
        { "a user token without principal type", TokenSpec.User("owner-x") with { PrincipalType = null } },
        { "a service principal type with the user audience", TokenSpec.User("owner-x") with { PrincipalType = "service" } },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => fixture.TenantMerchant.Heal();

    [Theory]
    [InlineData("GET", "products")]
    [InlineData("POST", "products")]
    [InlineData("GET", "brands")]
    [InlineData("POST", "brands")]
    public async Task MissingOrGarbageTokens_Are401(string method, string collection)
    {
        foreach (var token in new string?[] { null, "not-a-token", "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0." })
        {
            using var client = fixture.CreateClient(token);
            using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/v1/catalog/tenants/{Guid.CreateVersion7()}/{collection}");
            using var response = await client.SendAsync(request, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Theory]
    [MemberData(nameof(TokensRejectedAtAuthentication))]
    public async Task InvalidTokens_Are401(string reason, TokenSpec spec)
    {
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(spec));
        using var response = await client.GetAsync($"/api/v1/catalog/tenants/{Guid.CreateVersion7()}/products", Token);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{reason}: {response.StatusCode}");
    }

    [Theory]
    [MemberData(nameof(TokensRejectedByTheMerchantPolicy))]
    public async Task OtherCallers_Are403_WithoutAskingTenantMerchant(string reason, TokenSpec spec)
    {
        var calls = fixture.TenantMerchant.VerificationCalls;
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(spec));
        using var response = await client.GetAsync($"/api/v1/catalog/tenants/{Guid.CreateVersion7()}/products", Token);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{reason}: {response.StatusCode}");
        Assert.Equal(calls, fixture.TenantMerchant.VerificationCalls);
    }

    [Fact]
    public async Task ANonMember_SeesTheSameAnswerAsForAnUnknownTenant()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out _, out _);

        using var foreign = await fixture.SendAsync("stranger", HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantId}/products");
        using var unknown = await fixture.SendAsync("stranger", HttpMethod.Get, $"/api/v1/catalog/tenants/{Guid.CreateVersion7()}/products");

        var first = await CatalogApi.AssertProblemAsync(foreign, HttpStatusCode.NotFound, "tenant-not-found");
        var second = await CatalogApi.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "tenant-not-found");
        Assert.Equal(first.GetProperty("title").GetString(), second.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ForgedRoleAndTenantClaims_AreIgnored()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out _, out var staff);
        var forged = TokenSpec.User("stranger") with
        {
            ExtraClaims = new Dictionary<string, object> { ["role"] = "MerchantOwner", ["roles"] = new[] { "MerchantOwner" }, ["tenant_id"] = tenantId.ToString(), ["tenant_context"] = tenantId.ToString() },
        };
        var forgedStaff = TokenSpec.User(staff) with { ExtraClaims = new Dictionary<string, object> { ["role"] = "MerchantOwner" } };

        using var asStranger = fixture.CreateClient(fixture.Tokens.CreateToken(forged));
        using var strangerResponse = await asStranger.GetAsync($"/api/v1/catalog/tenants/{tenantId}/products", Token);
        using var asStaff = fixture.CreateClient(fixture.Tokens.CreateToken(forgedStaff));
        using var staffResponse = await asStaff.PostAsync($"/api/v1/catalog/tenants/{tenantId}/brands", System.Net.Http.Json.JsonContent.Create(new { slug = "forged", name = "Forged" }), Token);

        await CatalogApi.AssertProblemAsync(strangerResponse, HttpStatusCode.NotFound, "tenant-not-found");
        await CatalogApi.AssertProblemAsync(staffResponse, HttpStatusCode.Forbidden, "permission-denied");
    }

    [Fact]
    public async Task AnotherTenantsProduct_CannotBeReadOrChanged_ThroughOnesOwnTenant()
    {
        fixture.RequireAvailable();
        var tenantA = fixture.NewTenant(out var ownerA, out _);
        var tenantB = fixture.NewTenant(out var ownerB, out _);
        var productB = await fixture.CreateProductAsync(tenantB, ownerB, CatalogApi.ProductBody("tenant-b-product"));
        var idB = productB.GetProperty("id").GetGuid();

        using var read = await fixture.SendAsync(ownerA, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantA}/products/{idB}");
        using var update = await fixture.SendAsync(ownerA, HttpMethod.Put, $"/api/v1/catalog/tenants/{tenantA}/products/{idB}", CatalogApi.ProductBody("hijacked"), CatalogApi.IfMatch(1));
        using var archive = await fixture.SendAsync(ownerA, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantA}/products/{idB}/archive");
        using var viaTenantB = await fixture.SendAsync(ownerA, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantB}/products/{idB}");

        await CatalogApi.AssertProblemAsync(read, HttpStatusCode.NotFound, "product-not-found");
        await CatalogApi.AssertProblemAsync(update, HttpStatusCode.NotFound, "product-not-found");
        await CatalogApi.AssertProblemAsync(archive, HttpStatusCode.NotFound, "product-not-found");
        await CatalogApi.AssertProblemAsync(viaTenantB, HttpStatusCode.NotFound, "tenant-not-found");

        using var unchanged = await fixture.SendAsync(ownerB, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantB}/products/{idB}");
        var product = await CatalogApi.ReadAsync(unchanged);
        Assert.Equal(("tenant-b-product", "Draft", 1L), (product.GetProperty("slug").GetString(), product.GetProperty("status").GetString(), product.GetProperty("version").GetInt64()));
    }

    [Fact]
    public async Task ARemovedMember_LosesAccessAtTheNextRequest()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out _, out var staff);
        using (var before = await fixture.SendAsync(staff, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantId}/products"))
        {
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        }

        fixture.TenantMerchant.RemoveMember(tenantId, staff);

        using var after = await fixture.SendAsync(staff, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantId}/products");
        await CatalogApi.AssertProblemAsync(after, HttpStatusCode.NotFound, "tenant-not-found");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task WithoutAVerdict_NothingIsReadOrWritten(HttpStatusCode failure)
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        fixture.TenantMerchant.FailWith = failure;

        using var create = await fixture.SendAsync(owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", CatalogApi.ProductBody("never-written"), CatalogApi.Key());
        using var list = await fixture.SendAsync(owner, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantId}/products");

        var problem = await CatalogApi.AssertProblemAsync(create, HttpStatusCode.ServiceUnavailable, "membership-unavailable");
        Assert.Equal("5", create.Headers.RetryAfter?.ToString());
        Assert.True(problem.TryGetProperty("correlationId", out _));
        await CatalogApi.AssertProblemAsync(list, HttpStatusCode.ServiceUnavailable, "membership-unavailable");

        fixture.TenantMerchant.Heal();
        using var afterwards = await fixture.SendAsync(owner, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantId}/products");
        Assert.Equal(0, (await CatalogApi.ReadAsync(afterwards)).GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task ErrorResponses_LeakNoInternals()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        using var malformed = await fixture.SendAsync(
            owner, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json"), CatalogApi.Key());

        var body = await malformed.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", body, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Text.Json", body, StringComparison.Ordinal);
    }
}

[CollectionDefinition(nameof(MerchantSecurityCollection), DisableParallelization = true)]
public sealed class MerchantSecurityCollection;
