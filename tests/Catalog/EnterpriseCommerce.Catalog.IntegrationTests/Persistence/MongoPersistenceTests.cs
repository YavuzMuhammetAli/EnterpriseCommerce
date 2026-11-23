using EnterpriseCommerce.Catalog.Application.Brands;
using EnterpriseCommerce.Catalog.Application.Categories;
using EnterpriseCommerce.Catalog.Application.Common;
using EnterpriseCommerce.Catalog.Application.Products;
using EnterpriseCommerce.Catalog.Domain.Products;
using EnterpriseCommerce.Catalog.Infrastructure.Persistence;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Persistence;

/// <summary>
/// The MongoDB persistence on a real server (ADR-024 sections 1, 10 and 11): the stored shape, every unique index under
/// concurrent writes, optimistic concurrency and repeatable index creation.
/// </summary>
public sealed class MongoPersistenceTests(CatalogMongoFixture fixture) : IClassFixture<CatalogMongoFixture>
{
    private IProductStore Products => fixture.Factory.Services.GetRequiredService<IProductStore>();

    private ICategoryStore Categories => fixture.Factory.Services.GetRequiredService<ICategoryStore>();

    private IBrandStore Brands => fixture.Factory.Services.GetRequiredService<IBrandStore>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryIndex_IsCreatedWithItsName()
    {
        fixture.RequireAvailable();

        foreach (var (collection, names) in CatalogIndexes.Names())
        {
            var existing = await fixture.Mongo.IndexNamesAsync(collection);
            Assert.Equal(names.Append("_id_").Order(StringComparer.Ordinal), existing.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task IndexCreation_IsRepeatable()
    {
        fixture.RequireAvailable();

        await using var second = fixture.CreateFactory();
        await CatalogMongoFixture.WaitUntilReadyAsync(second);

        Assert.Equal(IndexSetupStatus.Ready, second.Services.GetRequiredService<CatalogIndexState>().Status);
    }

    [Fact]
    public async Task AProduct_RoundTripsExactly_WithStandardGuidsDecimalsAndDates()
    {
        fixture.RequireAvailable();
        var product = CatalogSamples.Product(Guid.CreateVersion7(), "round-trip", "RT-1", "rt-2");

        Assert.Equal(WriteOutcome.Written, await Products.InsertAsync(product, Token));
        var stored = await Products.GetAsync(product.TenantId, product.Id, Token);

        Assert.NotNull(stored);
        Assert.Equal((product.Content.Slug, product.Content.Name, product.Content.Description), (stored.Content.Slug, stored.Content.Name, stored.Content.Description));
        Assert.Equal(product.Content.Variants.Select(v => (v.Id, v.Sku.Value, v.Price.Amount, v.Price.Currency)), stored.Content.Variants.Select(v => (v.Id, v.Sku.Value, v.Price.Amount, v.Price.Currency)));
        Assert.Equal(product.CreatedAt, stored.CreatedAt);
        Assert.Equal(ProductStatus.Draft, stored.Status);

        var raw = await fixture.Mongo.Database.GetCollection<BsonDocument>("products").Find(new BsonDocument("_id", new BsonBinaryData(product.Id, GuidRepresentation.Standard))).SingleAsync(Token);
        Assert.Equal(BsonBinarySubType.UuidStandard, raw["tenantId"].AsBsonBinaryData.SubType);
        Assert.Equal(BsonType.Decimal128, raw["variants"][0]["amount"].BsonType);
        Assert.Equal(BsonType.DateTime, raw["createdAt"].BsonType);
        Assert.Equal("RT-2", raw["variants"][1]["skuKey"].AsString);
        Assert.False(raw.Contains("stock"));
    }

    [Fact]
    public async Task AnotherTenantsProduct_IsNeverFound()
    {
        fixture.RequireAvailable();
        var product = CatalogSamples.Product(Guid.CreateVersion7(), "owned", "OWN-1");
        await Products.InsertAsync(product, Token);

        Assert.Null(await Products.GetAsync(Guid.CreateVersion7(), product.Id, Token));
    }

    [Fact]
    public async Task Skus_AreUniquePerTenant_WithoutRegardToCase_AcrossProducts()
    {
        fixture.RequireAvailable();
        var tenantId = Guid.CreateVersion7();

        Assert.Equal(WriteOutcome.Written, await Products.InsertAsync(CatalogSamples.Product(tenantId, "first", "SKU-A"), Token));
        Assert.Equal(WriteOutcome.SkuTaken, await Products.InsertAsync(CatalogSamples.Product(tenantId, "second", "sku-a"), Token));
        Assert.Equal(WriteOutcome.Written, await Products.InsertAsync(CatalogSamples.Product(Guid.CreateVersion7(), "first", "SKU-A"), Token));
    }

    [Fact]
    public async Task DraftsWithoutVariants_DoNotCollide()
    {
        fixture.RequireAvailable();
        var tenantId = Guid.CreateVersion7();

        Assert.Equal(WriteOutcome.Written, await Products.InsertAsync(CatalogSamples.Product(tenantId, "empty-one"), Token));
        Assert.Equal(WriteOutcome.Written, await Products.InsertAsync(CatalogSamples.Product(tenantId, "empty-two"), Token));
    }

    [Fact]
    public async Task ConcurrentInserts_OfOneSlug_WriteExactlyOneProduct()
    {
        fixture.RequireAvailable();
        var tenantId = Guid.CreateVersion7();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Products.InsertAsync(CatalogSamples.Product(tenantId, "race-slug", $"RACE-{i}"), Token)));

        Assert.Single(outcomes, outcome => outcome == WriteOutcome.Written);
        Assert.All(outcomes.Where(outcome => outcome != WriteOutcome.Written), outcome => Assert.Equal(WriteOutcome.SlugTaken, outcome));
    }

    [Fact]
    public async Task ConcurrentInserts_OfOneSku_WriteExactlyOneProduct()
    {
        fixture.RequireAvailable();
        var tenantId = Guid.CreateVersion7();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Products.InsertAsync(CatalogSamples.Product(tenantId, $"sku-race-{i}", "SAME-SKU"), Token)));

        Assert.Single(outcomes, outcome => outcome == WriteOutcome.Written);
        Assert.All(outcomes.Where(outcome => outcome != WriteOutcome.Written), outcome => Assert.Equal(WriteOutcome.SkuTaken, outcome));
    }

    [Fact]
    public async Task ACreationKey_IsUniquePerTenantAndSubject()
    {
        fixture.RequireAvailable();
        var tenantId = Guid.CreateVersion7();

        Assert.Equal(WriteOutcome.Written, await Products.InsertAsync(CatalogSamples.Product(tenantId, CatalogSamples.Input("key-one"), "key-1"), Token));
        Assert.Equal(WriteOutcome.CreationKeyTaken, await Products.InsertAsync(CatalogSamples.Product(tenantId, CatalogSamples.Input("key-two"), "key-1"), Token));
        Assert.NotNull(await Products.FindByCreationKeyAsync(tenantId, "owner-1", "key-1", Token));
        Assert.Null(await Products.FindByCreationKeyAsync(tenantId, "someone-else", "key-1", Token));
    }

    [Fact]
    public async Task ConcurrentReplaces_OfOneVersion_WriteExactlyOnce()
    {
        fixture.RequireAvailable();
        var product = CatalogSamples.Product(Guid.CreateVersion7(), "versioned", "VER-1");
        await Products.InsertAsync(product, Token);

        var copies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Products.GetAsync(product.TenantId, product.Id, Token)));
        var outcomes = await Task.WhenAll(copies.Select(copy =>
        {
            copy!.Archive("owner-1", CatalogSamples.Now);
            return Products.ReplaceAsync(copy, 1, Token);
        }));

        Assert.Single(outcomes, outcome => outcome == WriteOutcome.Written);
        Assert.Equal(2, (await Products.GetAsync(product.TenantId, product.Id, Token))!.Version);
        Assert.Equal(2, copies.Single(copy => copy!.Version == 2)!.Version);
    }

    [Fact]
    public async Task CategoryAndBrandSlugs_AreUniqueInTheirScope()
    {
        fixture.RequireAvailable();
        var slug = $"cat-{Guid.NewGuid():N}"[..20];

        Assert.Equal(WriteOutcome.Written, await Categories.InsertAsync(CatalogSamples.RootCategory(slug), Token));
        Assert.Equal(WriteOutcome.SlugTaken, await Categories.InsertAsync(CatalogSamples.RootCategory(slug), Token));

        var tenantId = Guid.CreateVersion7();
        Assert.Equal(WriteOutcome.Written, await Brands.InsertAsync(CatalogSamples.Brand(tenantId, "acme"), Token));
        Assert.Equal(WriteOutcome.SlugTaken, await Brands.InsertAsync(CatalogSamples.Brand(tenantId, "acme"), Token));
        Assert.Equal(WriteOutcome.Written, await Brands.InsertAsync(CatalogSamples.Brand(Guid.CreateVersion7(), "acme"), Token));
    }

    [Fact]
    public async Task AnUnknownIndexConflict_KeepsTheServiceNotReady_AndRefusesWrites()
    {
        fixture.RequireAvailable();
        await using var conflicted = new MongoTestDatabase();
        await conflicted.Database.GetCollection<BsonDocument>("products").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("unrelated"), new CreateIndexOptions { Name = "ux_products_tenant_slug" }),
            cancellationToken: Token);

        await using var factory = fixture.CreateFactory(builder => builder.UseSetting("Catalog:Database:DatabaseName", conflicted.DatabaseName));
        var state = factory.Services.GetRequiredService<CatalogIndexState>();
        await Eventually.TrueAsync(() => Task.FromResult(state.Status == IndexSetupStatus.Failed), TimeSpan.FromSeconds(30), "the index setup fails");

        using var client = factory.CreateClient();
        using var ready = await client.GetAsync("/health/ready", Token);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        await Assert.ThrowsAsync<DataStoreUnavailableException>(() =>
            factory.Services.GetRequiredService<IProductStore>().InsertAsync(CatalogSamples.Product(Guid.CreateVersion7(), "never", "N-1"), Token));
        Assert.Contains(fixture.Logs.Events, logEvent => logEvent.MessageTemplate.Text.Contains("failed permanently", StringComparison.Ordinal));
    }
}
