using System.Diagnostics.CodeAnalysis;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.Domain.Categories;

/// <summary>Why a category cannot be created, retired or reactivated.</summary>
public enum CategoryOutcome
{
    Changed,
    Unchanged,

    /// <summary>The parent does not exist or is retired.</summary>
    ParentUnavailable,

    /// <summary>The parent is at the deepest allowed level.</summary>
    TooDeep,

    /// <summary>A category with active children cannot be retired.</summary>
    HasActiveChildren,
}

/// <summary>
/// A category of the platform-wide taxonomy (ADR-024 section 6). Its parent is fixed at creation and must already
/// exist and be active, so a parent is always older than its child: the tree is acyclic by construction and a category
/// can never be its own parent. Its ancestry therefore never changes, which lets products store it. A category is
/// never deleted; it is retired.
/// </summary>
public sealed class Category
{
    public const int MaxDepth = 5;
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;

    private Category(
        Guid id,
        Slug slug,
        string name,
        string? description,
        Guid? parentId,
        IReadOnlyList<Guid> ancestorIds,
        CatalogStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        Id = id;
        Slug = slug;
        Name = name;
        Description = description;
        ParentId = parentId;
        AncestorIds = ancestorIds;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    public Guid Id { get; }

    public Slug Slug { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public Guid? ParentId { get; }

    /// <summary>The ancestors, root first. Immutable.</summary>
    public IReadOnlyList<Guid> AncestorIds { get; }

    /// <summary>1 for a root category, at most <see cref="MaxDepth"/>.</summary>
    public int Depth => AncestorIds.Count + 1;

    public CatalogStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>The category and its ancestors: what a product assigned to it stores for filtering.</summary>
    public IEnumerable<Guid> TreeIds => AncestorIds.Append(Id);

    /// <summary>Validates the editable details of a category.</summary>
    public static bool TryCreateDetails(
        string? slug,
        string? name,
        string? description,
        [NotNullWhen(true)] out CategoryDetails? details,
        out ValidationErrors errors)
    {
        errors = new ValidationErrors();
        if (!Slug.TryCreate(slug, out var validSlug))
        {
            errors.Add("slug", $"The slug must be {Slug.Rule}");
        }

        if (!CatalogText.TryRequired(name, MaxNameLength, out var validName))
        {
            errors.Add("name", $"The name is required: 1 to {MaxNameLength} characters on one line.");
        }

        if (!CatalogText.TryOptional(description, MaxDescriptionLength, multiLine: false, out var validDescription))
        {
            errors.Add("description", $"The description is at most {MaxDescriptionLength} characters on one line.");
        }

        details = errors.IsEmpty ? new CategoryDetails(validSlug!, validName, validDescription) : null;
        return details is not null;
    }

    /// <summary>
    /// A new active category. <paramref name="parent"/> is the loaded parent, or <c>null</c> for a root category;
    /// <paramref name="parentRequested"/> says whether the request named one (which then had to be found).
    /// </summary>
    public static CategoryOutcome TryCreate(
        Guid id,
        CategoryDetails details,
        bool parentRequested,
        Category? parent,
        DateTimeOffset now,
        [NotNullWhen(true)] out Category? category)
    {
        ArgumentNullException.ThrowIfNull(details);
        category = null;

        if (parentRequested && parent is not { Status: CatalogStatus.Active })
        {
            return CategoryOutcome.ParentUnavailable;
        }

        if (parent is not null && parent.Depth >= MaxDepth)
        {
            return CategoryOutcome.TooDeep;
        }

        IReadOnlyList<Guid> ancestors = parent is null ? [] : [.. parent.TreeIds];
        category = new Category(id, details.Slug, details.Name, details.Description, parent?.Id, ancestors, CatalogStatus.Active, now, now, 1);
        return CategoryOutcome.Changed;
    }

    /// <summary>Restores a stored category, which satisfied every rule when it was written.</summary>
    public static Category Rehydrate(
        Guid id,
        Slug slug,
        string name,
        string? description,
        Guid? parentId,
        IReadOnlyList<Guid> ancestorIds,
        CatalogStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version) =>
        new(id, slug, name, description, parentId, ancestorIds, status, createdAt, updatedAt, version);

    /// <summary>Replaces the editable details. The parent never changes.</summary>
    public void Update(CategoryDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        Slug = details.Slug;
        Name = details.Name;
        Description = details.Description;
        UpdatedAt = now;
    }

    /// <summary>Retires the category, unless it still has active children.</summary>
    public CategoryOutcome Retire(bool hasActiveChildren, DateTimeOffset now)
    {
        if (Status == CatalogStatus.Retired)
        {
            return CategoryOutcome.Unchanged;
        }

        if (hasActiveChildren)
        {
            return CategoryOutcome.HasActiveChildren;
        }

        Status = CatalogStatus.Retired;
        UpdatedAt = now;
        return CategoryOutcome.Changed;
    }

    /// <summary>Reactivates the category; a child category needs an active parent (<paramref name="parent"/>).</summary>
    public CategoryOutcome Reactivate(Category? parent, DateTimeOffset now)
    {
        if (Status == CatalogStatus.Active)
        {
            return CategoryOutcome.Unchanged;
        }

        if (ParentId is not null && (parent is null || parent.Id != ParentId || parent.Status != CatalogStatus.Active))
        {
            return CategoryOutcome.ParentUnavailable;
        }

        Status = CatalogStatus.Active;
        UpdatedAt = now;
        return CategoryOutcome.Changed;
    }

    /// <summary>Called by the store after a successful write.</summary>
    public void AcceptStoredVersion(long version)
    {
        if (version <= Version)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A write always advances the stored version.");
        }

        Version = version;
    }
}

/// <summary>The validated, editable details of a category.</summary>
public sealed record CategoryDetails(Slug Slug, string Name, string? Description);
