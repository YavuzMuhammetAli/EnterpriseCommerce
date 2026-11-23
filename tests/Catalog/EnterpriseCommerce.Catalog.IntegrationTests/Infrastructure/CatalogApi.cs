using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseCommerce.Catalog.Application.Categories;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Domain.Categories;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

/// <summary>Request helpers of the Catalog API tests.</summary>
public static class CatalogApi
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A new tenant whose owner, admin and staff members are known to the stub Tenant &amp; Merchant.</summary>
    public static Guid NewTenant(this CatalogApiFixture fixture, out string owner, out string staff)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var tenantId = Guid.CreateVersion7();
        owner = $"owner-{tenantId:N}";
        staff = $"staff-{tenantId:N}";
        fixture.TenantMerchant.SetMember(tenantId, owner, "MerchantOwner");
        fixture.TenantMerchant.SetMember(tenantId, $"admin-{tenantId:N}", "MerchantAdmin");
        fixture.TenantMerchant.SetMember(tenantId, staff, "MerchantStaff");
        return tenantId;
    }

    /// <summary>Sends a request as a merchant-portal user, with optional headers.</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        this CatalogApiFixture fixture,
        string? subject,
        HttpMethod method,
        string path,
        object? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        using var client = fixture.CreateClient(subject is null ? null : fixture.UserToken(subject));
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : body as HttpContent ?? JsonContent.Create(body) };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request, Token);
    }

    /// <summary>Creates a product as <paramref name="subject"/> and returns it (asserting 201).</summary>
    public static async Task<JsonElement> CreateProductAsync(this CatalogApiFixture fixture, Guid tenantId, string subject, object body)
    {
        using var response = await fixture.SendAsync(
            subject, HttpMethod.Post, $"/api/v1/catalog/tenants/{tenantId}/products", body, Key());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return await ReadAsync(response);
    }

    /// <summary>A valid product body.</summary>
    public static object ProductBody(string slug, IReadOnlyList<Guid>? categoryIds = null, string sku = "", Guid? brandId = null, string name = "") => new
    {
        slug,
        name = name.Length > 0 ? name : $"Product {slug}",
        shortDescription = "A short description.",
        description = "A longer description.",
        brandId,
        categoryIds = categoryIds ?? [],
        attributes = new[] { new { key = "material", value = "Cotton" } },
        variants = new[]
        {
            new { sku = sku.Length > 0 ? sku : $"SKU-{slug}", options = new[] { new { key = "size", value = "M" } }, price = new { amount = 19.99m, currency = "EUR" }, isActive = true },
        },
    };

    /// <summary>
    /// Waits (a bounded poll) until the stored clock has passed <paramref name="moment"/>, so the next write gets a later
    /// millisecond and time-ordered assertions never depend on the random part of a GUID tie-breaker.
    /// </summary>
    public static Task PassAsync(DateTimeOffset moment) =>
        Eventually.TrueAsync(() => Task.FromResult(CatalogClock.Now(TimeProvider.System) > moment), TimeSpan.FromSeconds(1), "the clock passes the last write");

    public static Dictionary<string, string> Key(string? value = null) => new() { ["Idempotency-Key"] = value ?? Guid.NewGuid().ToString("D") };

    public static Dictionary<string, string> IfMatch(long version) => new() { ["If-Match"] = $"\"{version}\"" };

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(Token), cancellationToken: Token);
        return document.RootElement.Clone();
    }

    /// <summary>Asserts a problem response and returns its body.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal($"https://errors.enterprisecommerce.example/catalog/{code}", problem.GetProperty("type").GetString());
        return problem;
    }

    /// <summary>A platform category written directly through the store (the taxonomy API is tested separately).</summary>
    public static async Task<Category> SeedCategoryAsync(this CatalogApiFixture fixture, Category? parent = null, bool retired = false)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var slug = $"c-{Guid.NewGuid():N}";
        Assert.True(Category.TryCreateDetails(slug, $"Category {slug[..8]}", null, out var details, out _));
        Assert.Equal(CategoryOutcome.Changed, Category.TryCreate(Guid.CreateVersion7(), details, parent is not null, parent, CatalogSamples.Now, out var category));
        if (retired)
        {
            category!.Retire(hasActiveChildren: false, CatalogSamples.Now);
        }

        Assert.Equal(WriteOutcome.Written, await fixture.Factory.Services.GetRequiredService<ICategoryStore>().InsertAsync(category!, Token));
        return category!;
    }
}
