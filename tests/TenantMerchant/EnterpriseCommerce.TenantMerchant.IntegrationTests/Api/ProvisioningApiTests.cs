using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary><c>POST /internal/v1/tenants</c> and <c>GET /internal/v1/tenants/{id}</c> against SQL Server.</summary>
public sealed class ProvisioningApiTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    [Fact]
    public async Task Provision_CreatesTheTenantWithItsOwner()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.AdminToken());

        using var response = await TenantMerchantApiFixture.ProvisionAsync(client, Body("created", owner: "owner-created"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = await response.Content.ReadFromJsonAsync<ProvisionedTenantDto>(TestContext.Current.CancellationToken);
        Assert.Equal($"/internal/v1/tenants/{tenant!.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal("created", tenant.Slug);
        Assert.Equal("Active", tenant.Status);
        Assert.Equal("created store", tenant.MerchantProfile.DisplayName);
        Assert.Equal("owner-created", tenant.OwnerMembership!.SubjectId);
        Assert.Equal("MerchantOwner", tenant.OwnerMembership.Role);
        Assert.Equal("Active", tenant.OwnerMembership.Status);

        using var read = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var stored = await read.Content.ReadFromJsonAsync<ProvisionedTenantDto>(TestContext.Current.CancellationToken);
        Assert.Equal(tenant.Id, stored!.Id);
        Assert.Equal(tenant.OwnerMembership.Id, stored.OwnerMembership!.Id);
    }

    [Fact]
    public async Task Provision_RetryWithTheSameKey_ReturnsTheOriginal()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.AdminToken());
        var key = Guid.NewGuid().ToString("D");

        using var first = await TenantMerchantApiFixture.ProvisionAsync(client, Body("replayed"), key);
        using var retry = await TenantMerchantApiFixture.ProvisionAsync(client, Body("replayed"), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var created = await first.Content.ReadFromJsonAsync<ProvisionedTenantDto>(TestContext.Current.CancellationToken);
        var replayed = await retry.Content.ReadFromJsonAsync<ProvisionedTenantDto>(TestContext.Current.CancellationToken);
        Assert.Equal(created!.Id, replayed!.Id);
        Assert.Equal(created.OwnerMembership!.Id, replayed.OwnerMembership!.Id);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'replayed'"));
    }

    [Fact]
    public async Task Provision_SameKeyDifferentRequest_Is422()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.AdminToken());
        var key = Guid.NewGuid().ToString("D");

        using var first = await TenantMerchantApiFixture.ProvisionAsync(client, Body("key-reuse"), key);
        using var reuse = await TenantMerchantApiFixture.ProvisionAsync(client, Body("key-reuse-other"), key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertProblemAsync(reuse, HttpStatusCode.UnprocessableEntity, "idempotency-key-reused");
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'key-reuse-other'"));
    }

    [Fact]
    public async Task Provision_TakenSlug_Is409()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.AdminToken());

        using var first = await TenantMerchantApiFixture.ProvisionAsync(client, Body("taken"));
        using var second = await TenantMerchantApiFixture.ProvisionAsync(client, Body("taken", owner: "someone-else"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "tenant-slug-taken");
    }

    [Fact]
    public async Task Provision_InvalidFields_Are400PerField()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.AdminToken());

        using var response = await TenantMerchantApiFixture.ProvisionAsync(
            client,
            new { slug = "Bad Slug", displayName = "", ownerSubjectId = "has space", actorReference = "x y" });

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        var errors = problem.RootElement.GetProperty("errors");
        Assert.Equal(["actorReference", "displayName", "ownerSubjectId", "slug"], errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    public async Task Provision_WithoutAValidIdempotencyKey_Is400(string? key)
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.AdminToken());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/tenants") { Content = JsonContent.Create(Body("no-key")) };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        using var problem = await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'no-key'"));
    }

    [Theory]
    [InlineData("""{"slug":"smuggled","displayName":"x","ownerSubjectId":"u","actorReference":"a","role":"MerchantAdmin"}""")]
    [InlineData("""{"slug":"smuggled","displayName":"x","ownerSubjectId":"u","actorReference":"a","memberships":[{"subjectId":"attacker","role":"MerchantOwner"}]}""")]
    [InlineData("""{"slug":"smuggled","displayName":"x","ownerSubjectId":"u","actorReference":"a","status":"Active","id":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("not json")]
    public async Task Provision_RejectsFieldsOutsideTheContract(string json)
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.AdminToken());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/tenants")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'smuggled'"));
    }

    [Fact]
    public async Task GetInternal_UnknownTenant_Is404()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.AdminToken());

        using var response = await client.GetAsync($"/internal/v1/tenants/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "tenant-not-found");
    }

    [Fact]
    public async Task ConcurrentRequests_ForOneSlug_CreateExactlyOneTenant()
    {
        fixture.RequireAvailable();
        const int callers = 8;

        var responses = await Task.WhenAll(Enumerable.Range(0, callers).Select(async index =>
        {
            using var client = fixture.CreateClient(fixture.AdminToken());
            using var response = await TenantMerchantApiFixture.ProvisionAsync(client, Body("contended", owner: $"owner-{index}"));
            return response.StatusCode;
        }));

        Assert.Equal(1, responses.Count(status => status == HttpStatusCode.Created));
        Assert.Equal(callers - 1, responses.Count(status => status == HttpStatusCode.Conflict));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'contended'"));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM Memberships m JOIN Tenants t ON t.Id = m.TenantId WHERE t.Slug = 'contended'"));
    }

    [Fact]
    public async Task ConcurrentRetries_WithOneKey_CreateExactlyOneTenant()
    {
        fixture.RequireAvailable();
        const int callers = 8;
        var key = Guid.NewGuid().ToString("D");

        var results = await Task.WhenAll(Enumerable.Range(0, callers).Select(async _ =>
        {
            using var client = fixture.CreateClient(fixture.AdminToken());
            using var response = await TenantMerchantApiFixture.ProvisionAsync(client, Body("retried"), key);
            var tenant = await response.Content.ReadFromJsonAsync<ProvisionedTenantDto>(TestContext.Current.CancellationToken);
            return (response.StatusCode, tenant!.Id);
        }));

        Assert.Equal(1, results.Count(result => result.StatusCode == HttpStatusCode.Created));
        Assert.Equal(callers - 1, results.Count(result => result.StatusCode == HttpStatusCode.OK));
        Assert.Single(results.Select(result => result.Id).Distinct());
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'retried'"));
    }

    [Fact]
    public async Task Provision_WhenTheMembershipInsertFails_Is503AndLeavesNothing()
    {
        fixture.RequireAvailable();
        await fixture.Database.ExecuteAsync("ALTER TABLE Memberships ADD CONSTRAINT CK_Test_RejectApiSubject CHECK (SubjectId <> N'reject-api')");
        try
        {
            using var client = fixture.CreateClient(fixture.AdminToken());

            using var response = await TenantMerchantApiFixture.ProvisionAsync(client, Body("failing", owner: "reject-api"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.True(response.Headers.Contains("Retry-After"));
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'failing'"));
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM MerchantProfiles p JOIN Tenants t ON t.Id = p.TenantId WHERE t.Slug = 'failing'"));
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("CK_Test_RejectApiSubject", body, StringComparison.Ordinal);
            Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.Database.ExecuteAsync("ALTER TABLE Memberships DROP CONSTRAINT CK_Test_RejectApiSubject");
        }
    }

    internal static object Body(string slug, string owner = "owner-1") =>
        new { slug, displayName = $"{slug} store", ownerSubjectId = owner, actorReference = "admin:42" };

    internal static async Task<JsonDocument> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal($"https://errors.enterprisecommerce.example/tenant-merchant/{code}", problem.RootElement.GetProperty("type").GetString());
        return problem;
    }
}
