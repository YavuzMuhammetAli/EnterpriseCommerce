using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Api;

/// <summary>
/// The platform category taxonomy (CATALOG_API_CONTRACT.md sections 3 and 6, ADR-024 section 6): internal writes by the
/// allow-listed service client only, the parent rules, retire and reactivate, and public reads of active categories.
/// </summary>
public sealed class CategoryApiTests(CatalogMongoFixture fixture) : IClassFixture<CatalogMongoFixture>
{
    private const string InternalPath = "/internal/v1/catalog/categories";

    public static TheoryData<string, TokenSpec> TokensRejectedByTheTaxonomyPolicy() => new()
    {
        { "a merchant user", TokenSpec.User("owner-x") },
        { "a client that is not allow-listed", new TokenSpec { Subject = "svc-rogue", ClientId = "svc-rogue" } },
        { "the allow-listed client without the scope", new TokenSpec { Scope = "svc.catalog.other" } },
        { "sub differs from client_id", new TokenSpec { Subject = "someone" } },
        { "a user principal type", new TokenSpec { PrincipalType = "user" } },
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Children_InheritTheAncestryOfTheirParents()
    {
        fixture.RequireAvailable();
        var root = await CreateAsync(Slug("root"));
        var child = await CreateAsync(Slug("child"), root.Id);
        var grandchild = await CreateAsync(Slug("grandchild"), child.Id);

        Assert.Equal(1, root.Depth);
        Assert.Equal([root.Id, child.Id], grandchild.AncestorIds);
        Assert.Equal((child.Id, 3), (grandchild.ParentId, grandchild.Depth));
    }

    [Fact]
    public async Task TheParent_MustExistBeActive_AndKeepTheTreeShallow()
    {
        fixture.RequireAvailable();
        using (var unknown = await SendAsync(HttpMethod.Post, InternalPath, new { slug = Slug("orphan"), name = "Orphan", parentId = Guid.CreateVersion7() }))
        {
            await CatalogApi.AssertProblemAsync(unknown, HttpStatusCode.Conflict, "parent-unavailable");
        }

        var level = await CreateAsync(Slug("level-1"));
        for (var depth = 2; depth <= 5; depth++)
        {
            level = await CreateAsync(Slug($"level-{depth}"), level.Id);
        }

        using var tooDeep = await SendAsync(HttpMethod.Post, InternalPath, new { slug = Slug("level-6"), name = "Too deep", parentId = level.Id });
        await CatalogApi.AssertProblemAsync(tooDeep, HttpStatusCode.Conflict, "category-too-deep");
    }

    [Fact]
    public async Task Slugs_AreUniqueAcrossThePlatform()
    {
        fixture.RequireAvailable();
        var slug = Slug("unique");
        await CreateAsync(slug);

        using var duplicate = await SendAsync(HttpMethod.Post, InternalPath, new { slug, name = "Again" });
        await CatalogApi.AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "slug-taken");
    }

    [Fact]
    public async Task Updates_ChangeDetailsOnly_WithTheEntityTag()
    {
        fixture.RequireAvailable();
        var parent = await CreateAsync(Slug("parent"));
        var category = await CreateAsync(Slug("editable"));
        var path = $"{InternalPath}/{category.Id}";

        using (var moved = await SendAsync(HttpMethod.Put, path, new { slug = category.Slug, name = "Moved", parentId = parent.Id }, CatalogApi.IfMatch(1)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        }

        using (var missing = await SendAsync(HttpMethod.Put, path, new { slug = category.Slug, name = "Renamed" }))
        {
            await CatalogApi.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "precondition-required");
        }

        using var renamed = await SendAsync(HttpMethod.Put, path, new { slug = category.Slug, name = "Renamed", description = "Now described." }, CatalogApi.IfMatch(1));
        var body = (await renamed.Content.ReadFromJsonAsync<CategoryDto>(CatalogApi.Json, Token))!;
        Assert.Equal(("Renamed", 2L, (Guid?)null), (body.Name, body.Version, body.ParentId));
    }

    [Fact]
    public async Task Retiring_And_Reactivating_FollowTheTree()
    {
        fixture.RequireAvailable();
        var parent = await CreateAsync(Slug("retire-parent"));
        var child = await CreateAsync(Slug("retire-child"), parent.Id);

        await AssertStateAsync(parent.Id, "retire", HttpStatusCode.Conflict, "category-has-active-children");
        Assert.Equal("Retired", await StateAsync(child.Id, "retire"));
        Assert.Equal("Retired", await StateAsync(parent.Id, "retire"));
        await AssertStateAsync(child.Id, "reactivate", HttpStatusCode.Conflict, "parent-unavailable");
        Assert.Equal("Active", await StateAsync(parent.Id, "reactivate"));
        Assert.Equal("Active", await StateAsync(child.Id, "reactivate"));
        Assert.Equal("Active", await StateAsync(child.Id, "reactivate"));
    }

    [Fact]
    public async Task PublicReads_ShowActiveCategoriesOnly_WithoutInternalFields()
    {
        fixture.RequireAvailable();
        var active = await CreateAsync(Slug("public-active"));
        var retired = await CreateAsync(Slug("public-retired"));
        await StateAsync(retired.Id, "retire");

        using var anonymous = fixture.CreateClient();
        var list = await anonymous.GetFromJsonAsync<JsonElement>("/api/v1/catalog/categories?pageSize=100", Token);
        var ids = list.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(active.Id, ids);
        Assert.DoesNotContain(retired.Id, ids);
        Assert.All(list.GetProperty("items").EnumerateArray(), item => Assert.False(item.TryGetProperty("status", out _) || item.TryGetProperty("version", out _)));

        using var retiredRead = await anonymous.GetAsync($"/api/v1/catalog/categories/{retired.Id}", Token);
        await CatalogApi.AssertProblemAsync(retiredRead, HttpStatusCode.NotFound, "category-not-found");
        using var internalRead = await SendAsync(HttpMethod.Get, $"{InternalPath}/{retired.Id}");
        Assert.Equal(HttpStatusCode.OK, internalRead.StatusCode);
    }

    [Fact]
    public async Task PublicReads_IgnoreAnyToken()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient("garbage-token");
        using var response = await client.GetAsync("/api/v1/catalog/categories", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheInternalApi_RequiresAToken()
    {
        using var client = fixture.CreateClient();
        using var response = await client.PostAsJsonAsync(InternalPath, new { slug = "no-token", name = "No token" }, Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(TokensRejectedByTheTaxonomyPolicy))]
    public async Task OtherCallers_Are403(string reason, TokenSpec spec)
    {
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(spec));
        using var response = await client.PostAsJsonAsync(InternalPath, new { slug = "forbidden", name = "Forbidden" }, Token);

        Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{reason}: {response.StatusCode}");
    }

    private static string Slug(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 13, 100)];

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var client = fixture.CreateClient(fixture.TaxonomyToken());
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request, Token);
    }

    private async Task<CategoryDto> CreateAsync(string slug, Guid? parentId = null)
    {
        using var response = await SendAsync(HttpMethod.Post, InternalPath, new { slug, name = slug, parentId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(CatalogApi.Json, Token))!;
    }

    private async Task<string> StateAsync(Guid categoryId, string command)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{InternalPath}/{categoryId}/{command}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(CatalogApi.Json, Token))!.Status;
    }

    private async Task AssertStateAsync(Guid categoryId, string command, HttpStatusCode status, string code)
    {
        using var response = await SendAsync(HttpMethod.Post, $"{InternalPath}/{categoryId}/{command}");
        await CatalogApi.AssertProblemAsync(response, status, code);
    }

    private sealed record CategoryDto(Guid Id, string Slug, string Name, Guid? ParentId, IReadOnlyList<Guid> AncestorIds, int Depth, string Status, long Version);
}
