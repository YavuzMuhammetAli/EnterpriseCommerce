namespace EnterpriseCommerce.Gateway.IntegrationTests;

/// <summary>
/// No public route forwards an internal service path (finding P3-1, PHASE_3_TENANT_MERCHANT.md section 10): the real
/// Gateway, its committed Development routes and real Kestrel request parsing, with a recording backend behind every
/// cluster. "Blocked" means the Gateway answered itself and nothing reached the backend.
/// </summary>
public sealed class InternalPathExposureTests(KestrelGatewayFixture gateway) : IClassFixture<KestrelGatewayFixture>
{
    private const string TenantId = "0196f0a0-0000-7000-8000-000000000001";

    public static TheoryData<string> Hosts() =>
    [
        "localhost:5000",
        "localhost:7000",
        "id.localhost:7000",
        "storefront.localhost:7000",
        "merchant.localhost:7000",
        "admin.localhost:7000",
        "unknown.example",
    ];

    [Theory]
    [InlineData("/identity/health", "localhost:5000", "/health")]
    [InlineData("/.well-known/openid-configuration", "id.localhost:7000", "/.well-known/openid-configuration")]
    [InlineData("/account/login", "id.localhost:7000", "/account/login")]
    [InlineData("/connect/token", "id.localhost:7000", "/connect/token")]
    public async Task PublicIdentityEndpoints_AreStillForwarded(string target, string host, string expectedBackendTarget)
    {
        var response = await gateway.SendAsync("GET", target, host);

        Assert.True(response.Forwarded, $"{target} on {host} was not forwarded (status {response.Status}).");
        Assert.Equal(200, response.Status);
        Assert.Equal(expectedBackendTarget, response.BackendTarget);
    }

    [Theory]
    [InlineData("POST", "/identity/internal/v1/accounts/1/disable")]
    [InlineData("POST", "/identity/internal/v1/accounts/1/reactivate")]
    [InlineData("GET", "/identity/internal")]
    [InlineData("GET", "/identity/internal/")]
    public async Task TheIdentityPrefixRoute_NeverReachesInternalEndpoints(string method, string target)
    {
        var response = await gateway.SendAsync(method, target, "localhost:5000");

        AssertBlocked(response, target);
        Assert.Equal(404, response.Status);
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task InternalPaths_AreBlockedOnEveryHost(string host)
    {
        foreach (var target in new[]
        {
            "/identity/internal/v1/accounts/1/disable",
            "/internal/v1/accounts/1/disable",
            "/internal/v1/tenants",
            $"/internal/v1/tenants/{TenantId}",
            $"/internal/v1/tenants/{TenantId}/membership-verifications",
            "/internal/v1/catalog/categories",
            $"/internal/v1/catalog/tenants/{TenantId}/variant-verifications",
            $"/internal/v1/inventory/tenants/{TenantId}/reservations",
            $"/internal/v1/inventory/tenants/{TenantId}/reservations/{TenantId}/commit",
            "/internal/v1/cart",
            "/internal/v1/carts",
        })
        {
            var response = await gateway.SendAsync("POST", target, host);

            AssertBlocked(response, $"{target} on {host}");
            Assert.Equal(404, response.Status);
        }
    }

    [Theory]
    [InlineData("/identity/INTERNAL/v1/accounts/1/disable")]
    [InlineData("/identity/Internal/V1/Accounts/1/disable")]
    [InlineData("/identity/internal%2Fv1%2Faccounts%2F1%2Fdisable")]
    [InlineData("/identity/internal%2fv1/accounts/1/disable")]
    [InlineData("/identity/%69nternal/v1/accounts/1/disable")]
    [InlineData("/identity/%2569nternal/v1/accounts/1/disable")]
    [InlineData("/identity/internal%252Fv1/accounts/1/disable")]
    [InlineData("/identity//internal/v1/accounts/1/disable")]
    [InlineData("/identity/./internal/v1/accounts/1/disable")]
    [InlineData("/identity/other/../internal/v1/accounts/1/disable")]
    [InlineData("/identity/%2e/internal/v1/accounts/1/disable")]
    [InlineData("/identity/other/%2e%2e/internal/v1/accounts/1/disable")]
    [InlineData("/identity/%5Cinternal/v1/accounts/1/disable")]
    [InlineData("/INTERNAL/v1/accounts/1/disable")]
    [InlineData("//internal/v1/accounts/1/disable")]
    [InlineData("/./internal/v1/accounts/1/disable")]
    [InlineData("/x/../internal/v1/accounts/1/disable")]
    [InlineData("/internal%2Fv1/accounts/1/disable")]
    [InlineData("/%69nternal/v1/accounts/1/disable")]
    [InlineData("/api/v1/tenants/../../../internal/v1/tenants")]
    [InlineData("/api/v1/merchant-invitations/../../../internal/v1/tenants")]
    [InlineData("/api/v1/merchant-invitations/%2e%2e/%2e%2e/%2e%2e/internal/v1/accounts/1/email-verification")]
    [InlineData("/internal/v1/accounts/1/email-verification")]
    [InlineData("/api/v1/catalog/../../../internal/v1/catalog/categories")]
    [InlineData("/api/v1/catalog/%2e%2e/%2e%2e/%2e%2e/internal/v1/catalog/categories")]
    [InlineData("/api/v1/catalog/products/../../../../internal/v1/tenants/0196f0a0-0000-7000-8000-000000000001/membership-verifications")]
    [InlineData("/api/v1/catalog/..%2F..%2F..%2Finternal/v1/catalog/categories")]
    [InlineData("/INTERNAL/v1/catalog/categories")]
    [InlineData($"/api/v1/inventory/../../../internal/v1/inventory/tenants/{TenantId}/reservations")]
    [InlineData($"/api/v1/inventory/%2e%2e/%2e%2e/%2e%2e/internal/v1/inventory/tenants/{TenantId}/reservations")]
    [InlineData($"/api/v1/inventory/tenants/{TenantId}/items/..%2F..%2F..%2F..%2F..%2F..%2Finternal/v1/inventory/tenants/{TenantId}/reservations")]
    [InlineData($"/api/v1/inventory/tenants/{TenantId}/items/../../../../../../internal/v1/catalog/tenants/{TenantId}/variant-verifications")]
    [InlineData($"/Internal/V1/Inventory/tenants/{TenantId}/reservations")]
    [InlineData($"/%69nternal/v1/inventory/tenants/{TenantId}/reservations")]
    [InlineData("/api/v1/cart/../../../internal/v1/cart")]
    [InlineData("/api/v1/cart/%2e%2e/%2e%2e/%2e%2e/internal/v1/inventory/tenants/0196f0a0-0000-7000-8000-000000000001/reservations")]
    [InlineData("/api/v1/cart/items/..%2F..%2F..%2F..%2Finternal/v1/catalog/categories")]
    [InlineData("/api/v1/cart/items/%2e%2e%2f%2e%2e%2f%2e%2e%2f%2e%2e%2finternal/v1/tenants")]
    [InlineData("/api/v1/cart/%252e%252e/%252e%252e/%252e%252e/internal/v1/cart")]
    [InlineData("/api/v1/cart/..%5C..%5C..%5Cinternal/v1/cart")]
    [InlineData("/INTERNAL/v1/Cart")]
    [InlineData("http://localhost:5000/internal/v1/accounts/1/disable")]
    [InlineData("http://localhost:5000/identity/internal/v1/accounts/1/disable")]
    public async Task AlternatePathForms_CannotBypassTheBlock(string target)
    {
        foreach (var host in new[] { "localhost:5000", "id.localhost:7000", "localhost:7000" })
        {
            var response = await gateway.SendAsync("POST", target, host);

            // Kestrel may refuse a malformed target itself (400); either way the request must not be forwarded.
            AssertBlocked(response, $"{target} on {host}");
            Assert.Contains(response.Status, new[] { 400, 404 });
        }
    }

    [Theory]
    [InlineData($"/api/v1/tenants/{TenantId}")]
    [InlineData($"/api/v1/tenants/{TenantId}/memberships?page=2&pageSize=50")]
    [InlineData("/api/v1/tenants")]
    [InlineData($"/api/v1/tenants/{TenantId}/invitations?status=Pending")]
    public async Task TheTenantMemberApi_IsStillForwarded(string target)
    {
        var response = await gateway.SendAsync("GET", target, "localhost:7000");

        Assert.True(response.Forwarded, $"{target} was not forwarded (status {response.Status}).");
        Assert.Equal(200, response.Status);
        Assert.Equal(target, response.BackendTarget);
    }

    [Theory]
    [InlineData("GET", "/api/v1/catalog/products?q=tea&attr=color:red&page=2")]
    [InlineData("GET", $"/api/v1/catalog/products/{TenantId}")]
    [InlineData("GET", "/api/v1/catalog/categories")]
    [InlineData("POST", $"/api/v1/catalog/tenants/{TenantId}/products")]
    [InlineData("PUT", $"/api/v1/catalog/tenants/{TenantId}/brands/{TenantId}")]
    public async Task TheCatalogApi_IsForwarded(string method, string target)
    {
        var response = await gateway.SendAsync(method, target, "localhost:7000");

        Assert.True(response.Forwarded, $"{target} was not forwarded (status {response.Status}).");
        Assert.Equal(200, response.Status);
        Assert.Equal(target, response.BackendTarget);
    }

    [Theory]
    [InlineData("GET", $"/api/v1/inventory/tenants/{TenantId}/items?sku=SKU-1&page=2")]
    [InlineData("POST", $"/api/v1/inventory/tenants/{TenantId}/items")]
    [InlineData("POST", $"/api/v1/inventory/tenants/{TenantId}/items/{TenantId}/adjustments")]
    [InlineData("GET", $"/api/v1/inventory/tenants/{TenantId}/items/{TenantId}/movements")]
    public async Task TheInventoryApi_IsForwarded(string method, string target)
    {
        var response = await gateway.SendAsync(method, target, "localhost:7000");

        Assert.True(response.Forwarded, $"{target} was not forwarded (status {response.Status}).");
        Assert.Equal(200, response.Status);
        Assert.Equal(target, response.BackendTarget);
    }

    [Theory]
    [InlineData("GET", "/api/v1/cart")]
    [InlineData("POST", "/api/v1/cart/items")]
    [InlineData("PUT", $"/api/v1/cart/items/{TenantId}")]
    [InlineData("DELETE", $"/api/v1/cart/items/{TenantId}")]
    [InlineData("DELETE", "/api/v1/cart/items")]
    [InlineData("POST", "/api/v1/cart/refresh")]
    public async Task TheCartApi_IsForwarded(string method, string target)
    {
        var response = await gateway.SendAsync(method, target, "localhost:7000");

        Assert.True(response.Forwarded, $"{target} was not forwarded (status {response.Status}).");
        Assert.Equal(200, response.Status);
        Assert.Equal(target, response.BackendTarget);
    }

    [Fact]
    public async Task SelfServiceOnboarding_IsForwarded()
    {
        // POST /api/v1/tenants (Phase 3 S3) uses the existing tenant route, which has no method restriction.
        var response = await gateway.SendAsync("POST", "/api/v1/tenants", "localhost:7000");

        Assert.True(response.Forwarded, $"Onboarding was not forwarded (status {response.Status}).");
        Assert.Equal("/api/v1/tenants", response.BackendTarget);
    }

    [Theory]
    [InlineData("/api/v1/tenants/../../../internal/v1/tenants")]
    [InlineData("/api/v1/tenants/%2e%2e/%2e%2e/%2e%2e/internal/v1/tenants")]
    public async Task SelfServiceOnboarding_CannotBeTurnedIntoInternalProvisioning(string target)
    {
        // The internal provisioning endpoint is the same resource name under /internal: no POST path form may reach it.
        var response = await gateway.SendAsync("POST", target, "localhost:7000");

        AssertBlocked(response, target);
        Assert.Contains(response.Status, new[] { 400, 404 });
    }

    [Fact]
    public async Task AnEncodedSlashAfterTheTenantPrefix_StaysOneInertSegment()
    {
        // "..%2f" is not a separator: the backend receives it re-encoded, as one literal segment under /api, which no
        // Tenant & Merchant route matches. It never becomes a path under /internal.
        var response = await gateway.SendAsync("POST", "/api/v1/tenants/..%2finternal/v1/tenants", "localhost:7000");

        if (response.Forwarded)
        {
            Assert.Equal("/api/v1/tenants/..%252finternal/v1/tenants", response.BackendTarget);
        }
    }

    [Fact]
    public async Task InvitationAcceptance_IsForwarded()
    {
        var response = await gateway.SendAsync("POST", "/api/v1/merchant-invitations/accept", "localhost:7000");

        Assert.True(response.Forwarded, $"Acceptance was not forwarded (status {response.Status}).");
        Assert.Equal("/api/v1/merchant-invitations/accept", response.BackendTarget);
    }

    [Theory]
    [InlineData("/identity/internalized/path", "localhost:5000", "/internalized/path")]
    [InlineData($"/api/v1/tenants/{TenantId}/internal", "localhost:7000", $"/api/v1/tenants/{TenantId}/internal")]
    public async Task PathsThatOnlyContainInternal_AreNotOverBlocked(string target, string host, string expectedBackendTarget)
    {
        var response = await gateway.SendAsync("GET", target, host);

        Assert.True(response.Forwarded, $"{target} was not forwarded (status {response.Status}).");
        Assert.Equal(expectedBackendTarget, response.BackendTarget);
    }

    [Fact]
    public async Task NoConfiguredRoute_ExposesAnInternalPath()
    {
        // For every committed route, probe what its own match and transforms would turn into an internal path.
        var probed = 0;
        foreach (var (name, path, hosts) in DevelopmentConfiguration.Routes())
        {
            var prefix = path.Replace("{**catch-all}", string.Empty, StringComparison.Ordinal).TrimEnd('/');
            foreach (var host in hosts.Count > 0 ? hosts : ["localhost:5000", "localhost:7000"])
            {
                foreach (var target in new[] { $"{prefix}/internal/v1/accounts/1/disable", $"{prefix}/internal/v1/tenants", "/internal/v1/tenants" })
                {
                    foreach (var method in new[] { "GET", "POST" })
                    {
                        var response = await gateway.SendAsync(method, target, host);
                        probed++;
                        Assert.False(
                            response.Forwarded && IsInternal(response.BackendTarget),
                            $"Route {name}: {method} {target} on {host} reached the backend as {response.BackendTarget}.");
                    }
                }
            }
        }

        Assert.True(probed >= 6 * 3 * 2, $"Only {probed} probes ran; the route set looks incomplete.");
        Assert.DoesNotContain(gateway.Received, IsInternal);
    }

    private void AssertBlocked(GatewayResponse response, string description)
    {
        Assert.False(response.Forwarded, $"{description} reached the backend as {response.BackendTarget}.");
        Assert.DoesNotContain(gateway.Received, IsInternal);
    }

    /// <summary>The backend's view: the first path segment, percent-decoded, is "internal".</summary>
    private static bool IsInternal(string? target)
    {
        if (target is null)
        {
            return false;
        }

        var path = target.Split('?')[0];
        for (var pass = 0; pass < 4; pass++)
        {
            path = Uri.UnescapeDataString(path);
        }

        var first = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(first, "internal", StringComparison.OrdinalIgnoreCase);
    }
}
