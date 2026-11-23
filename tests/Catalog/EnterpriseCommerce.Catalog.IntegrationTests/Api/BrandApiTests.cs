using System.Net;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>Tenant brands end to end on MongoDB (CATALOG_API_CONTRACT.md section 5).</summary>
public sealed class BrandApiTests(CatalogMongoFixture fixture) : IClassFixture<CatalogMongoFixture>
{
    [Fact]
    public async Task Brands_AreCreatedUpdatedRetiredAndListed_PerTenant()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out var staff);
        var basePath = $"/api/v1/catalog/tenants/{tenantId}/brands";

        using var created = await fixture.SendAsync(owner, HttpMethod.Post, basePath, new { slug = "acme", name = "Acme", description = "Tools." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var brand = await CatalogApi.ReadAsync(created);
        var path = $"{basePath}/{brand.GetProperty("id").GetGuid()}";
        Assert.Equal((tenantId, "Active"), (brand.GetProperty("tenantId").GetGuid(), brand.GetProperty("status").GetString()));

        using (var duplicate = await fixture.SendAsync(owner, HttpMethod.Post, basePath, new { slug = "acme", name = "Acme again" }))
        {
            await CatalogApi.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "slug-taken");
        }

        using (var noTag = await fixture.SendAsync(owner, HttpMethod.Put, path, new { slug = "acme", name = "ACME" }))
        {
            await CatalogApi.AssertProblemAsync(noTag, HttpStatusCode.PreconditionRequired, "precondition-required");
        }

        using (var updated = await fixture.SendAsync(owner, HttpMethod.Put, path, new { slug = "acme-tools", name = "ACME" }, CatalogApi.IfMatch(1)))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.Equal("acme-tools", (await CatalogApi.ReadAsync(updated)).GetProperty("slug").GetString());
        }

        using (var retired = await fixture.SendAsync(owner, HttpMethod.Post, $"{path}/retire"))
        {
            Assert.Equal("Retired", (await CatalogApi.ReadAsync(retired)).GetProperty("status").GetString());
        }

        using (var again = await fixture.SendAsync(owner, HttpMethod.Post, $"{path}/retire"))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        }

        using (var reactivated = await fixture.SendAsync(owner, HttpMethod.Post, $"{path}/reactivate"))
        {
            Assert.Equal("Active", (await CatalogApi.ReadAsync(reactivated)).GetProperty("status").GetString());
        }

        using var list = await fixture.SendAsync(staff, HttpMethod.Get, basePath);
        var page = await CatalogApi.ReadAsync(list);
        Assert.Equal(1, page.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public async Task StaffMayReadBrands_ButNotManageThem()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out _, out var staff);

        using var create = await fixture.SendAsync(staff, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/brands", new { slug = "staff-brand", name = "Staff" });
        using var list = await fixture.SendAsync(staff, HttpMethod.Get, $"/api/v1/catalog/tenants/{tenantId}/brands");

        await CatalogApi.AssertProblemAsync(create, HttpStatusCode.Forbidden, "permission-denied");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task InvalidBrands_AndUnknownMembers_AreRefused()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _);
        var path = $"/api/v1/catalog/tenants/{tenantId}/brands";

        using var invalid = await fixture.SendAsync(owner, HttpMethod.Post, path, new { slug = "x", name = "" });
        using var overPosted = await fixture.SendAsync(owner, HttpMethod.Post, path, new { slug = "brand-ok", name = "Ok", tenantId = Guid.CreateVersion7() });

        var errors = (await CatalogApi.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation-failed")).GetProperty("errors");
        Assert.True(errors.TryGetProperty("slug", out _) && errors.TryGetProperty("name", out _));
        Assert.Equal(HttpStatusCode.BadRequest, overPosted.StatusCode);
    }
}
