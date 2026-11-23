using EnterpriseCommerce.Catalog.Domain.Common;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.UnitTests.Domain;

public sealed class ProductTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly Guid CategoryId = Guid.CreateVersion7();
    private static readonly ReferenceState AllActive = new(AllCategoriesActive: true, BrandActive: true);

    [Fact]
    public void ValidContent_IsAccepted_AndNewVariantsGetNewIds()
    {
        var content = Content(Input());

        Assert.Equal("blue-shirt", content.Slug.Value);
        Assert.Equal("Blue shirt", content.Name);
        var variant = Assert.Single(content.Variants);
        Assert.NotEqual(Guid.Empty, variant.Id);
        Assert.Equal("BS-M", variant.Sku.Value);
        Assert.True(variant.IsActive);
    }

    [Fact]
    public void EveryError_IsReportedWithItsField()
    {
        var input = new ProductContentInput(
            "Bad Slug", " ", null, null, Guid.Empty, [Guid.Empty], [new KeyValueInput("Bad", "")],
            [new VariantInput(null, "bad sku", null, 0, "JPY", null)]);

        Assert.False(ProductContent.TryCreate(input, [], Guid.CreateVersion7, out _, out var errors));
        var fields = errors.ToDictionary().Keys;
        Assert.Contains("slug", fields);
        Assert.Contains("name", fields);
        Assert.Contains("brandId", fields);
        Assert.Contains("categoryIds", fields);
        Assert.Contains("attributes[0].key", fields);
        Assert.Contains("attributes[0].value", fields);
        Assert.Contains("variants[0].sku", fields);
        Assert.Contains("variants[0].amount", fields);
        Assert.Contains("variants[0].currency", fields);
    }

    [Fact]
    public void SkusOfOneProduct_AreUniqueWithoutRegardToCase()
    {
        var input = Input() with
        {
            Variants = [Variant("abc-1", ("size", "s")), Variant("ABC-1", ("size", "m"))],
        };

        Assert.False(ProductContent.TryCreate(input, [], Guid.CreateVersion7, out _, out var errors));
        Assert.True(errors.Contains("variants"));
    }

    [Fact]
    public void Variants_NeedDistinctOptionSets()
    {
        var input = Input() with
        {
            Variants = [Variant("A-1", ("size", "M"), ("color", "Red")), Variant("A-2", ("color", "red"), ("size", "m"))],
        };

        Assert.False(ProductContent.TryCreate(input, [], Guid.CreateVersion7, out _, out _));
    }

    [Fact]
    public void Variants_ShareOneCurrency()
    {
        var input = Input() with
        {
            Variants = [Variant("A-1", ("size", "s")), Variant("A-2", ("size", "m")) with { Currency = "USD" }],
        };

        Assert.False(ProductContent.TryCreate(input, [], Guid.CreateVersion7, out _, out _));
    }

    [Fact]
    public void AKnownVariantId_IsKept_AndAnUnknownOneIsRefused()
    {
        var existing = Content(Input()).Variants[0];

        var kept = Content(Input() with { Variants = [Variant("BS-M") with { Id = existing.Id, Amount = 25 }] }, [existing]);
        Assert.Equal(existing.Id, kept.Variants[0].Id);
        Assert.Equal(25, kept.Variants[0].Price.Amount);

        Assert.False(ProductContent.TryCreate(
            Input() with { Variants = [Variant("BS-M") with { Id = Guid.CreateVersion7() }] }, [existing], Guid.CreateVersion7, out _, out var errors));
        Assert.True(errors.Contains("variants[0].id"));
    }

    [Fact]
    public void CategoriesAndAttributes_AreBounded_AndDistinct()
    {
        Assert.False(ProductContent.TryCreate(
            Input() with { CategoryIds = [.. Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7())] }, [], Guid.CreateVersion7, out _, out _));
        Assert.False(ProductContent.TryCreate(Input() with { CategoryIds = [CategoryId, CategoryId] }, [], Guid.CreateVersion7, out _, out _));
        Assert.False(ProductContent.TryCreate(
            Input() with { Attributes = [new KeyValueInput("color", "red"), new KeyValueInput("color", "blue")] }, [], Guid.CreateVersion7, out _, out _));
        Assert.False(ProductContent.TryCreate(
            Input() with { Attributes = [.. Enumerable.Range(0, 51).Select(i => new KeyValueInput($"k{i}", "v"))] }, [], Guid.CreateVersion7, out _, out _));
    }

    [Fact]
    public void TheEditableContent_HasNoTenant()
    {
        Assert.DoesNotContain(typeof(ProductContentInput).GetProperties(), property => property.Name.Contains("Tenant", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ProductContent).GetProperties(), property => property.Name.Contains("Tenant", StringComparison.Ordinal));
    }

    [Fact]
    public void ANewProduct_IsADraftOfItsTenant()
    {
        var product = NewProduct();

        Assert.Equal(ProductStatus.Draft, product.Status);
        Assert.Equal(TenantId, product.TenantId);
        Assert.Equal(1, product.Version);
        Assert.Equal("owner-1", product.CreatedBy);
        Assert.Null(product.PublishedAt);
    }

    [Fact]
    public void Publish_RequiresACategory_ActiveReferences_AndAnActiveVariant()
    {
        var noCategory = NewProduct(Input() with { CategoryIds = [] });
        Assert.Equal(TransitionOutcome.RequirementsNotMet, noCategory.Publish(AllActive, "owner-1", Now, out var unmet));
        Assert.Contains(PublicationRequirement.CategoryRequired, unmet);

        var product = NewProduct(Input() with { BrandId = Guid.CreateVersion7() });
        Assert.Equal(TransitionOutcome.RequirementsNotMet, product.Publish(new ReferenceState(false, false), "owner-1", Now, out unmet));
        Assert.Equal([PublicationRequirement.CategoryInactive, PublicationRequirement.BrandInactive], unmet);

        var inactive = NewProduct(Input() with { Variants = [Variant("A-1") with { IsActive = false }] });
        Assert.Equal(TransitionOutcome.RequirementsNotMet, inactive.Publish(AllActive, "owner-1", Now, out unmet));
        Assert.Equal([PublicationRequirement.ActiveVariantRequired], unmet);

        Assert.Equal(ProductStatus.Draft, product.Status);
    }

    [Fact]
    public void TheLifecycle_FollowsItsTransitions()
    {
        var product = NewProduct();
        var later = Now.AddMinutes(1);

        Assert.Equal(TransitionOutcome.Changed, product.Publish(AllActive, "admin-1", later, out _));
        Assert.Equal((ProductStatus.Published, later, "admin-1"), (product.Status, product.PublishedAt!.Value, product.UpdatedBy));
        Assert.Equal(TransitionOutcome.Unchanged, product.Publish(AllActive, "admin-1", later, out _));
        Assert.Equal(TransitionOutcome.InvalidTransition, product.Restore("admin-1", later));

        Assert.Equal(TransitionOutcome.Changed, product.Unpublish("admin-1", later));
        Assert.Equal(ProductStatus.Draft, product.Status);
        Assert.Equal(TransitionOutcome.Unchanged, product.Unpublish("admin-1", later));
        Assert.Equal(TransitionOutcome.Unchanged, product.Restore("admin-1", later));

        Assert.Equal(TransitionOutcome.Changed, product.Archive("admin-1", later));
        Assert.Equal(ProductStatus.Archived, product.Status);
        Assert.NotNull(product.ArchivedAt);
        Assert.Equal(TransitionOutcome.Unchanged, product.Archive("admin-1", later));
        Assert.Equal(TransitionOutcome.InvalidTransition, product.Publish(AllActive, "admin-1", later, out _));
        Assert.Equal(TransitionOutcome.InvalidTransition, product.Unpublish("admin-1", later));

        Assert.Equal(TransitionOutcome.Changed, product.Restore("admin-1", later));
        Assert.Equal(ProductStatus.Draft, product.Status);
        Assert.Null(product.ArchivedAt);
    }

    [Fact]
    public void APublishedProduct_CanBeArchivedDirectly()
    {
        var product = NewProduct();
        product.Publish(AllActive, "admin-1", Now, out _);

        Assert.Equal(TransitionOutcome.Changed, product.Archive("admin-1", Now));
    }

    [Fact]
    public void ChangesToAPublishedProduct_AreRevalidated()
    {
        var product = NewProduct();
        product.Publish(AllActive, "admin-1", Now, out _);
        var before = product.Content;

        var withoutCategory = Content(Input() with { CategoryIds = [] }, product.Content.Variants);
        Assert.Equal(ProductChangeOutcome.RequirementsNotMet, product.ReplaceContent(withoutCategory, [], AllActive, "staff-1", Now, out var unmet));
        Assert.Contains(PublicationRequirement.CategoryRequired, unmet);
        Assert.Same(before, product.Content);
        Assert.Equal(ProductStatus.Published, product.Status);

        var renamed = Content(Input() with { Name = "Navy shirt" }, product.Content.Variants);
        Assert.Equal(ProductChangeOutcome.Changed, product.ReplaceContent(renamed, [CategoryId], AllActive, "staff-1", Now, out _));
        Assert.Equal(("Navy shirt", "staff-1"), (product.Content.Name, product.UpdatedBy));
    }

    [Fact]
    public void ADraft_MayBeIncomplete()
    {
        var product = NewProduct();
        var incomplete = Content(Input() with { CategoryIds = [], Variants = [] });

        Assert.Equal(ProductChangeOutcome.Changed, product.ReplaceContent(incomplete, [], new ReferenceState(false, false), "staff-1", Now, out _));
    }

    [Fact]
    public void AnArchivedProduct_IsReadOnly()
    {
        var product = NewProduct();
        product.Archive("admin-1", Now);

        Assert.Equal(ProductChangeOutcome.Archived, product.ReplaceContent(Content(Input()), [CategoryId], AllActive, "staff-1", Now, out _));
        Assert.Equal(ProductChangeOutcome.Archived, product.ReplaceMedia([], "staff-1", Now));
    }

    [Fact]
    public void Media_KeepsKnownIds_AndRefusesUnsafeUrls()
    {
        var product = NewProduct();
        Assert.True(Product.TryCreateMedia(
            [new MediaInput(null, "https://cdn.example.com/a.jpg", "Front", "image/jpeg")], product.Media, Guid.CreateVersion7, out var media, out _));
        product.ReplaceMedia(media, "staff-1", Now);
        var id = product.Media[0].Id;

        Assert.True(Product.TryCreateMedia(
            [new MediaInput(null, "https://cdn.example.com/b.png", null, "image/png"), new MediaInput(id, "https://cdn.example.com/a.jpg", "Front", "image/jpeg")],
            product.Media, Guid.CreateVersion7, out var reordered, out _));
        Assert.Equal(id, reordered[1].Id);

        Assert.False(Product.TryCreateMedia(
            [new MediaInput(null, "https://169.254.169.254/x", null, "image/png")], product.Media, Guid.CreateVersion7, out _, out var errors));
        Assert.True(errors.Contains("media[0].url"));
        Assert.False(Product.TryCreateMedia(
            [.. Enumerable.Range(0, 21).Select(_ => new MediaInput(null, "https://cdn.example.com/a.jpg", null, "image/jpeg"))],
            product.Media, Guid.CreateVersion7, out _, out _));
    }

    [Fact]
    public void TheStoredVersion_OnlyMovesForward()
    {
        var product = NewProduct();
        product.AcceptStoredVersion(2);

        Assert.Equal(2, product.Version);
        Assert.Throws<ArgumentOutOfRangeException>(() => product.AcceptStoredVersion(2));
    }

    private static Product NewProduct(ProductContentInput? input = null) =>
        Product.Create(Guid.CreateVersion7(), TenantId, Content(input ?? Input()), [CategoryId], new ProductCreation("owner-1", "key-1", "hash"), Now);

    private static ProductContent Content(ProductContentInput input, IReadOnlyList<ProductVariant>? existing = null)
    {
        Assert.True(ProductContent.TryCreate(input, existing ?? [], Guid.CreateVersion7, out var content, out var errors), string.Join("; ", errors.ToDictionary().Keys));
        return content;
    }

    private static ProductContentInput Input() =>
        new("blue-shirt", "Blue shirt", "A shirt.", "Cotton.\nMachine washable.", null, [CategoryId], [new KeyValueInput("material", "Cotton")], [Variant("BS-M", ("size", "M"))]);

    private static VariantInput Variant(string sku, params (string Key, string Value)[] options) =>
        new(null, sku, [.. options.Select(option => new KeyValueInput(option.Key, option.Value))], 19.99m, "EUR", null);
}
