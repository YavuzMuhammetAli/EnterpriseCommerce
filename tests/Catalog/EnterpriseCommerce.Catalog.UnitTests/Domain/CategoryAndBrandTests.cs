using EnterpriseCommerce.Catalog.Domain.Brands;
using EnterpriseCommerce.Catalog.Domain.Categories;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.UnitTests.Domain;

public sealed class CategoryAndBrandTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AChild_InheritsItsParentsAncestry()
    {
        var root = Root("clothing");
        var child = Child(root, "shirts");
        var grandchild = Child(child, "t-shirts");

        Assert.Empty(root.AncestorIds);
        Assert.Equal([root.Id], child.AncestorIds);
        Assert.Equal([root.Id, child.Id], grandchild.AncestorIds);
        Assert.Equal(3, grandchild.Depth);
        Assert.Equal([root.Id, child.Id, grandchild.Id], grandchild.TreeIds);
    }

    [Fact]
    public void TheTree_IsAtMostFiveLevelsDeep()
    {
        var category = Root("level-one");
        for (var level = 2; level <= Category.MaxDepth; level++)
        {
            category = Child(category, $"level-{level}");
        }

        Assert.Equal(CategoryOutcome.TooDeep, Category.TryCreate(Guid.CreateVersion7(), Details("too-deep"), true, category, Now, out _));
    }

    [Fact]
    public void AParent_MustExistAndBeActive()
    {
        var retired = Root("retired");
        retired.Retire(hasActiveChildren: false, Now);

        Assert.Equal(CategoryOutcome.ParentUnavailable, Category.TryCreate(Guid.CreateVersion7(), Details("orphan"), true, null, Now, out _));
        Assert.Equal(CategoryOutcome.ParentUnavailable, Category.TryCreate(Guid.CreateVersion7(), Details("under-retired"), true, retired, Now, out _));
    }

    [Fact]
    public void TheParent_HasNoSetter_SoNoCategoryCanBecomeItsOwnAncestor()
    {
        Assert.Null(typeof(Category).GetProperty(nameof(Category.ParentId))!.SetMethod);
        Assert.Null(typeof(Category).GetProperty(nameof(Category.AncestorIds))!.SetMethod);
    }

    [Fact]
    public void Retiring_RequiresNoActiveChildren_AndReactivating_AnActiveParent()
    {
        var root = Root("home");
        var child = Child(root, "kitchen");

        Assert.Equal(CategoryOutcome.HasActiveChildren, root.Retire(hasActiveChildren: true, Now));
        Assert.Equal(CategoryOutcome.Changed, child.Retire(hasActiveChildren: false, Now));
        Assert.Equal(CategoryOutcome.Unchanged, child.Retire(hasActiveChildren: false, Now));
        Assert.Equal(CategoryOutcome.Changed, root.Retire(hasActiveChildren: false, Now));

        Assert.Equal(CategoryOutcome.ParentUnavailable, child.Reactivate(root, Now));
        Assert.Equal(CategoryOutcome.ParentUnavailable, child.Reactivate(null, Now));
        Assert.Equal(CategoryOutcome.Changed, root.Reactivate(null, Now));
        Assert.Equal(CategoryOutcome.Changed, child.Reactivate(root, Now));
        Assert.Equal(CategoryOutcome.Unchanged, child.Reactivate(root, Now));
    }

    [Fact]
    public void CategoryDetails_AreValidated()
    {
        Assert.False(Category.TryCreateDetails("Bad", "", new string('x', 501), out _, out var errors));
        Assert.Equal(["description", "name", "slug"], errors.ToDictionary().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Brands_BelongToOneTenant_AndAreRetiredNotDeleted()
    {
        var tenantId = Guid.CreateVersion7();
        Assert.True(Brand.TryCreateDetails("acme", "Acme", null, out var details, out _));
        var brand = Brand.Create(Guid.CreateVersion7(), tenantId, details, Now);

        Assert.Equal((tenantId, CatalogStatus.Active, 1L), (brand.TenantId, brand.Status, brand.Version));
        Assert.Equal(TransitionOutcome.Changed, brand.Retire(Now));
        Assert.Equal(TransitionOutcome.Unchanged, brand.Retire(Now));
        Assert.Equal(TransitionOutcome.Changed, brand.Reactivate(Now));
        Assert.Null(typeof(Brand).GetProperty(nameof(Brand.TenantId))!.SetMethod);
        Assert.Throws<ArgumentException>(() => Brand.Create(Guid.CreateVersion7(), Guid.Empty, details, Now));
    }

    private static CategoryDetails Details(string slug)
    {
        Assert.True(Category.TryCreateDetails(slug, slug, null, out var details, out _));
        return details;
    }

    private static Category Root(string slug)
    {
        Assert.Equal(CategoryOutcome.Changed, Category.TryCreate(Guid.CreateVersion7(), Details(slug), false, null, Now, out var category));
        return category!;
    }

    private static Category Child(Category parent, string slug)
    {
        Assert.Equal(CategoryOutcome.Changed, Category.TryCreate(Guid.CreateVersion7(), Details(slug), true, parent, Now, out var category));
        return category!;
    }
}
