using System.Net;
using System.Net.Http.Json;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// Tenant-level authorization from Tenant &amp; Merchant's own membership data (ADR-014): the tenant ID in the path is
/// never proof, cross-tenant access is invisible, and the role decides what a member may read.
/// </summary>
public sealed class MemberAccessTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    [Fact]
    public async Task TheOwner_ReadsTheTenantWithTheirMembership()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("owner-reads", "owner-reads-user");
        using var client = fixture.CreateClient(fixture.UserToken("owner-reads-user"));

        using var response = await client.GetAsync($"/api/v1/tenants/{tenantId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tenant = await response.Content.ReadFromJsonAsync<MemberTenantDto>(TestContext.Current.CancellationToken);
        Assert.Equal(tenantId, tenant!.Id);
        Assert.Equal("owner-reads", tenant.Slug);
        Assert.Equal("MerchantOwner", tenant.Membership.Role);
        Assert.Equal("owner-reads-user", tenant.Membership.SubjectId);
    }

    [Fact]
    public async Task AMemberOfAnotherTenant_SeesNeitherTenantNorMemberships()
    {
        fixture.RequireAvailable();
        var tenantA = await fixture.ProvisionTenantAsync("cross-a", "owner-cross-a");
        var tenantB = await fixture.ProvisionTenantAsync("cross-b", "owner-cross-b");
        using var ownerOfA = fixture.CreateClient(fixture.UserToken("owner-cross-a"));

        using var tenant = await ownerOfA.GetAsync($"/api/v1/tenants/{tenantB}", TestContext.Current.CancellationToken);
        using var memberships = await ownerOfA.GetAsync($"/api/v1/tenants/{tenantB}/memberships", TestContext.Current.CancellationToken);
        using var unknown = await ownerOfA.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        using var own = await ownerOfA.GetAsync($"/api/v1/tenants/{tenantA}", TestContext.Current.CancellationToken);

        // A tenant the caller is no member of looks exactly like one that does not exist.
        using var crossProblem = await ProvisioningApiTests.AssertProblemAsync(tenant, HttpStatusCode.NotFound, "tenant-not-found");
        using var listProblem = await ProvisioningApiTests.AssertProblemAsync(memberships, HttpStatusCode.NotFound, "tenant-not-found");
        using var unknownProblem = await ProvisioningApiTests.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "tenant-not-found");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Theory]
    [InlineData(MembershipStatus.Suspended)]
    [InlineData(MembershipStatus.Removed)]
    public async Task InactiveMembers_SeeNothing(MembershipStatus status)
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync($"inactive-{status.ToString().ToLowerInvariant()}", $"owner-inactive-{status}");
        await fixture.SeedMemberAsync(tenantId, $"inactive-{status}", MerchantRole.MerchantAdmin, status);
        using var client = fixture.CreateClient(fixture.UserToken($"inactive-{status}"));

        using var response = await client.GetAsync($"/api/v1/tenants/{tenantId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Staff_ReadTheTenantButNotTheMemberships()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("staff-limits", "owner-staff-limits");
        await fixture.SeedMemberAsync(tenantId, "staff-user", MerchantRole.MerchantStaff);
        using var client = fixture.CreateClient(fixture.UserToken("staff-user"));

        using var tenant = await client.GetAsync($"/api/v1/tenants/{tenantId}", TestContext.Current.CancellationToken);
        using var memberships = await client.GetAsync($"/api/v1/tenants/{tenantId}/memberships", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, tenant.StatusCode);
        using var problem = await ProvisioningApiTests.AssertProblemAsync(memberships, HttpStatusCode.Forbidden, "permission-denied");
    }

    [Fact]
    public async Task Admins_ListTheCurrentMembershipsWithPaging()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("paged", "owner-paged");
        await fixture.SeedMemberAsync(tenantId, "admin-paged", MerchantRole.MerchantAdmin);
        for (var index = 0; index < 22; index++)
        {
            await fixture.SeedMemberAsync(tenantId, $"staff-paged-{index:D2}", MerchantRole.MerchantStaff);
        }

        await fixture.SeedMemberAsync(tenantId, "removed-paged", MerchantRole.MerchantStaff, MembershipStatus.Removed);
        using var client = fixture.CreateClient(fixture.UserToken("admin-paged"));

        var first = await client.GetFromJsonAsync<MembershipPageDto>($"/api/v1/tenants/{tenantId}/memberships", TestContext.Current.CancellationToken);
        var second = await client.GetFromJsonAsync<MembershipPageDto>($"/api/v1/tenants/{tenantId}/memberships?page=2", TestContext.Current.CancellationToken);
        var large = await client.GetFromJsonAsync<MembershipPageDto>($"/api/v1/tenants/{tenantId}/memberships?pageSize=100", TestContext.Current.CancellationToken);

        Assert.Equal(20, first!.Items.Count);
        Assert.Equal(1, first.Page);
        Assert.Equal(20, first.PageSize);
        Assert.Equal(24, first.TotalCount);
        Assert.Equal("owner-paged", first.Items[0].SubjectId);
        Assert.Equal(4, second!.Items.Count);
        Assert.Equal(2, second.Page);
        Assert.Equal(24, large!.Items.Count);
        Assert.DoesNotContain(large.Items, m => m.SubjectId == "removed-paged");
        Assert.Equal(24, large.Items.Select(m => m.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=0")]
    [InlineData("page=abc")]
    public async Task OutOfRangePaging_Is400(string query)
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync($"paging-{Math.Abs(query.GetHashCode(StringComparison.Ordinal))}", $"owner-paging-{query}");
        using var client = fixture.CreateClient(fixture.UserToken($"owner-paging-{query}"));

        using var response = await client.GetAsync($"/api/v1/tenants/{tenantId}/memberships?{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ANonGuidTenantId_IsNotFound()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.UserToken("anyone"));

        using var response = await client.GetAsync("/api/v1/tenants/acme", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
