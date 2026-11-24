using System.Net;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Api;

/// <summary>
/// Merchant authorization and tenant isolation (ADR-025, ADR-026 section 10, ADR-014 rule 4): the role matrix, the
/// tenant from the route never being proof, forged claims, token validation, and fail-closed behaviour when Tenant &amp;
/// Merchant or Catalog cannot answer.
/// </summary>
public sealed class MerchantSecurityTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    public static TheoryData<string, TokenSpec> TokensThatFailValidation() => new()
    {
        { "a token of another audience", TokenSpec.User("owner") with { Audiences = ["catalog-api"] } },
        { "a token signed with an unknown key", TokenSpec.User("owner") with { SignWithUnknownKey = true } },
        { "an expired token", TokenSpec.User("owner") with { ExpiresIn = TimeSpan.FromMinutes(-5) } },
        { "a token of another issuer", TokenSpec.User("owner") with { Issuer = "https://evil.test/" } },
        { "an id token instead of an access token", TokenSpec.User("owner") with { TokenType = "JWT" } },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OwnersAndAdminsManageStock_StaffOnlyReads()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out var admin, out var staff);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 5);
        var path = $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}";
        var product = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        fixture.Dependencies.SetVariant(tenantId, product, variant);

        using var staffCreates = await fixture.SendAsync(fixture.UserToken(staff), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 1 }, InventoryApiFixture.Key());
        using var staffAdjusts = await fixture.SendAsync(fixture.UserToken(staff), HttpMethod.Post, $"{path}/adjustments", new { quantityChange = 1, reason = "Receipt" }, InventoryApiFixture.Key());
        using var staffReads = await fixture.SendAsync(fixture.UserToken(staff), HttpMethod.Get, path);
        using var staffReadsMovements = await fixture.SendAsync(fixture.UserToken(staff), HttpMethod.Get, $"{path}/movements");
        using var adminAdjusts = await fixture.SendAsync(fixture.UserToken(admin), HttpMethod.Post, $"{path}/adjustments", new { quantityChange = 1, reason = "Receipt" }, InventoryApiFixture.Key());
        using var adminCreates = await fixture.SendAsync(fixture.UserToken(admin), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 1 }, InventoryApiFixture.Key());

        await InventoryApiFixture.AssertProblemAsync(staffCreates, HttpStatusCode.Forbidden, "permission-denied");
        await InventoryApiFixture.AssertProblemAsync(staffAdjusts, HttpStatusCode.Forbidden, "permission-denied");
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (staffReads.StatusCode, staffReadsMovements.StatusCode));
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (adminAdjusts.StatusCode, adminCreates.StatusCode));
    }

    [Fact]
    public async Task AnotherTenantsMember_SeesNothing_AndAnotherTenantsItemIsNeverFound()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var otherTenant = fixture.NewTenant(out var otherOwner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 5);
        var foreignItem = await fixture.CreateItemAsync(otherTenant, otherOwner, onHand: 5);

        // A member of another tenant: the tenant is answered exactly like a missing one.
        using var outsider = await fixture.SendAsync(fixture.UserToken(otherOwner), HttpMethod.Get, $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}");
        using var outsiderAdjusts = await fixture.SendAsync(fixture.UserToken(otherOwner), HttpMethod.Post, $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}/adjustments", new { quantityChange = -5, reason = "Loss" }, InventoryApiFixture.Key());
        using var unknownTenant = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Get, Items(Guid.CreateVersion7()));

        // A member of this tenant addressing another tenant's item through this tenant's route.
        using var crossItem = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Get, $"{Items(tenantId)}/{foreignItem.GetProperty("id").GetGuid()}");
        using var crossAdjust = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, $"{Items(tenantId)}/{foreignItem.GetProperty("id").GetGuid()}/adjustments", new { quantityChange = -5, reason = "Loss" }, InventoryApiFixture.Key());

        await InventoryApiFixture.AssertProblemAsync(outsider, HttpStatusCode.NotFound, "tenant-not-found");
        await InventoryApiFixture.AssertProblemAsync(outsiderAdjusts, HttpStatusCode.NotFound, "tenant-not-found");
        await InventoryApiFixture.AssertProblemAsync(unknownTenant, HttpStatusCode.NotFound, "tenant-not-found");
        await InventoryApiFixture.AssertProblemAsync(crossItem, HttpStatusCode.NotFound, "inventory-item-not-found");
        await InventoryApiFixture.AssertProblemAsync(crossAdjust, HttpStatusCode.NotFound, "inventory-item-not-found");
        Assert.Equal(5, await fixture.Database.ScalarAsync<int>($"SELECT OnHand FROM InventoryItems WHERE Id = '{foreignItem.GetProperty("id").GetGuid()}'"));
    }

    [Fact]
    public async Task ForgedTenantAndRoleClaims_AreIgnored()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out var staff);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 5);
        var path = $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}/adjustments";
        var forged = new Dictionary<string, object> { ["role"] = "MerchantOwner", ["roles"] = "MerchantOwner", ["tenant_id"] = tenantId.ToString(), ["ec_role"] = "MerchantOwner" };

        using var forgedStaff = await fixture.SendAsync(fixture.Tokens.CreateToken(TokenSpec.User(staff) with { ExtraClaims = forged }), HttpMethod.Post, path, new { quantityChange = 1, reason = "Receipt" }, InventoryApiFixture.Key());
        using var forgedStranger = await fixture.SendAsync(fixture.Tokens.CreateToken(TokenSpec.User("stranger") with { ExtraClaims = forged }), HttpMethod.Post, path, new { quantityChange = 1, reason = "Receipt" }, InventoryApiFixture.Key());

        await InventoryApiFixture.AssertProblemAsync(forgedStaff, HttpStatusCode.Forbidden, "permission-denied");
        await InventoryApiFixture.AssertProblemAsync(forgedStranger, HttpStatusCode.NotFound, "tenant-not-found");
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM StockMovements WHERE InventoryItemId = '{item.GetProperty("id").GetGuid()}'"));
    }

    [Fact]
    public async Task AMembershipChange_AppliesToTheNextRequest_BecauseVerdictsAreNeverCached()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 5);
        var path = $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}";
        var calls = fixture.Dependencies.TenantMerchant.CallCount;

        using var before = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Get, path);
        fixture.Dependencies.SetMember(tenantId, owner, "MerchantStaff");
        using var demoted = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, $"{path}/adjustments", new { quantityChange = 1, reason = "Receipt" }, InventoryApiFixture.Key());
        fixture.Dependencies.RemoveMember(tenantId, owner);
        using var removed = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await InventoryApiFixture.AssertProblemAsync(demoted, HttpStatusCode.Forbidden, "permission-denied");
        await InventoryApiFixture.AssertProblemAsync(removed, HttpStatusCode.NotFound, "tenant-not-found");
        Assert.Equal(calls + 3, fixture.Dependencies.TenantMerchant.CallCount);
    }

    [Theory]
    [MemberData(nameof(TokensThatFailValidation))]
    public async Task AnInvalidToken_IsUnauthorized(string token, TokenSpec spec)
    {
        _ = token;
        using var response = await fixture.SendAsync(fixture.Tokens.CreateToken(spec), HttpMethod.Get, Items(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAToken_TheCallerIsUnauthorized()
    {
        using var response = await fixture.SendAsync(null, HttpMethod.Get, Items(Guid.CreateVersion7()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AServiceToken_IsNeverAMerchant()
    {
        var tenantId = fixture.NewTenant(out _, out _, out _);

        using var serviceToken = await fixture.SendAsync(fixture.ServiceToken(), HttpMethod.Get, Items(tenantId));
        using var userOfAServiceClient = await fixture.SendAsync(fixture.Tokens.CreateToken(TokenSpec.User("owner") with { ClientId = "svc-test-order" }), HttpMethod.Get, Items(tenantId));

        Assert.Equal((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden), (serviceToken.StatusCode, userOfAServiceClient.StatusCode));
    }

    [Theory]
    [InlineData("status 500")]
    [InlineData("status 404")]
    [InlineData("malformed verdict")]
    [InlineData("active without a role")]
    [InlineData("tokens refused")]
    public async Task WithoutAMembershipVerdict_TheRequestFailsClosed_AndNothingIsWritten(string failure)
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 5);
        var path = $"{Items(tenantId)}/{item.GetProperty("id").GetGuid()}/adjustments";
        switch (failure)
        {
            case "status 500": fixture.Dependencies.TenantMerchant.FailWith = HttpStatusCode.InternalServerError; break;
            case "status 404": fixture.Dependencies.TenantMerchant.FailWith = HttpStatusCode.NotFound; break;
            case "malformed verdict": fixture.Dependencies.TenantMerchant.RawAnswer = """{"allowed":true}"""; break;
            case "active without a role": fixture.Dependencies.TenantMerchant.RawAnswer = """{"active":true,"role":null}"""; break;
            default: fixture.Dependencies.RefuseTokens = true; await RestartTokensAsync(); break;
        }

        try
        {
            using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, path, new { quantityChange = 1, reason = "Receipt" }, InventoryApiFixture.Key());

            await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "membership-unavailable");
            Assert.Equal("5", response.Headers.RetryAfter?.ToString());
            Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM StockMovements WHERE InventoryItemId = '{item.GetProperty("id").GetGuid()}'"));
        }
        finally
        {
            fixture.Dependencies.Heal();
        }
    }

    [Fact]
    public async Task ARevokedServiceToken_IsRenewedOnce()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        await fixture.CreateItemAsync(tenantId, owner, onHand: 1);
        fixture.Dependencies.TenantMerchant.RejectTokens = 1;
        var tokens = fixture.Dependencies.TokenRequests;

        using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Get, Items(tenantId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(tokens + 1, fixture.Dependencies.TokenRequests);
    }

    [Theory]
    [InlineData("status 503")]
    [InlineData("malformed verdict")]
    [InlineData("found without a status")]
    [InlineData("a SKU Inventory refuses")]
    public async Task WithoutACatalogVerdict_NoItemIsCreated(string failure)
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var product = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        fixture.Dependencies.SetVariant(tenantId, product, variant, sku: failure == "a SKU Inventory refuses" ? "BAD SKU" : "SKU-OK");
        switch (failure)
        {
            case "status 503": fixture.Dependencies.Catalog.FailWith = HttpStatusCode.ServiceUnavailable; break;
            case "malformed verdict": fixture.Dependencies.Catalog.RawAnswer = "[]"; break;
            case "found without a status": fixture.Dependencies.Catalog.RawAnswer = """{"found":true,"productStatus":null,"variantActive":true,"sku":"X"}"""; break;
        }

        try
        {
            using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 1 }, InventoryApiFixture.Key());

            if (failure == "a SKU Inventory refuses")
            {
                await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.Conflict, "variant-not-eligible");
            }
            else
            {
                await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "catalog-unavailable");
                Assert.Equal("5", response.Headers.RetryAfter?.ToString());
            }

            Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM InventoryItems WHERE TenantId = '{tenantId}'"));
        }
        finally
        {
            fixture.Dependencies.Heal();
        }
    }

    [Fact]
    public async Task TheCorrelationId_ReachesTenantMerchantAndCatalog_AndTheLedger()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var product = Guid.CreateVersion7();
        var variant = Guid.CreateVersion7();
        fixture.Dependencies.SetVariant(tenantId, product, variant);
        var headers = InventoryApiFixture.Key();
        headers["X-Correlation-ID"] = "corr-inventory-42";

        using var response = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, Items(tenantId), new { productId = product, variantId = variant, initialOnHand = 1 }, headers);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("corr-inventory-42", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal(("corr-inventory-42", "corr-inventory-42"), (fixture.Dependencies.TenantMerchant.LastCorrelationId, fixture.Dependencies.Catalog.LastCorrelationId));
        Assert.Equal("corr-inventory-42", await fixture.Database.ScalarAsync<string>($"SELECT CorrelationId FROM StockMovements WHERE TenantId = '{tenantId}'"));
        Assert.Contains("\"correlationId\":\"corr-inventory-42\"", await fixture.Database.ScalarAsync<string>($"SELECT Envelope FROM OutboxMessages WHERE MessageKey = '{tenantId}'"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLogs_NeverContainTokens()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var token = fixture.UserToken(owner);
        using (await fixture.SendAsync(token, HttpMethod.Get, Items(tenantId)))
        {
        }

        Assert.DoesNotContain(fixture.Logs.AllText(), text => text.Contains(token, StringComparison.Ordinal) || text.Contains("stub-service-token-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheIntegrations_MerchantRequestsFailClosedInDevelopment()
    {
        await using var unconfigured = fixture.CreateFactory(builder =>
        {
            builder.UseSetting("Inventory:ServiceClient:TokenEndpoint", string.Empty);
            builder.UseSetting("Inventory:ServiceClient:PrivateKeyPath", string.Empty);
            builder.UseSetting("Inventory:MembershipVerification:BaseAddress", string.Empty);
            builder.UseSetting("Inventory:CatalogVerification:BaseAddress", string.Empty);
        });
        using var client = InventoryApiFixture.CreateClient(unconfigured, fixture.UserToken("owner"));

        using var response = await client.GetAsync(Items(Guid.CreateVersion7()), Token);

        await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "membership-unavailable");
    }

    /// <summary>Drops the host's cached service tokens, so the next call asks the token endpoint.</summary>
    private Task RestartTokensAsync()
    {
        var provider = (EnterpriseCommerce.Inventory.Infrastructure.ServiceClients.InventoryServiceTokenProvider)fixture.Factory.Services.GetService(typeof(EnterpriseCommerce.Inventory.Infrastructure.ServiceClients.InventoryServiceTokenProvider))!;
        provider.Invalidate(StubDependencies.MembershipScope);
        provider.Invalidate(StubDependencies.CatalogScope);
        return Task.CompletedTask;
    }

    private static string Items(Guid tenantId) => $"/api/v1/inventory/tenants/{tenantId}/items";
}
