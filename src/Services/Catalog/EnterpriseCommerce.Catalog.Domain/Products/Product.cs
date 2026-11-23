using System.Diagnostics.CodeAnalysis;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.Domain.Products;

/// <summary>The publication lifecycle of a product (ADR-024 section 5).</summary>
public enum ProductStatus
{
    Draft,
    Published,
    Archived,
}

/// <summary>
/// The state of a product's references, checked by the Application layer against the category taxonomy and the
/// tenant's brands: whether every referenced category and the brand (if any) exist and are active.
/// </summary>
public sealed record ReferenceState(bool AllCategoriesActive, bool BrandActive);

/// <summary>Why a product cannot be published: stable codes, returned to the client.</summary>
public static class PublicationRequirement
{
    public const string CategoryRequired = "category-required";
    public const string CategoryInactive = "category-inactive";
    public const string BrandInactive = "brand-inactive";
    public const string ActiveVariantRequired = "active-variant-required";
}

/// <summary>The outcome of a change to a product's content or media.</summary>
public enum ProductChangeOutcome
{
    Changed,

    /// <summary>The product is archived and read-only.</summary>
    Archived,

    /// <summary>The product is published and the change would break a publication requirement.</summary>
    RequirementsNotMet,
}

/// <summary>Who created a product and with which idempotency key; kept for the product's lifetime.</summary>
public sealed record ProductCreation(string SubjectId, string IdempotencyKey, string RequestHash);

/// <summary>
/// A merchant-owned product, stored as one document with its variants, attributes and media (ADR-024 section 3), so
/// every change is one atomic write. The tenant is set at creation and never changes. Optimistic concurrency uses
/// <see cref="Version"/>, which the store advances on every successful write.
/// </summary>
public sealed class Product
{
    public const int MaxMedia = 20;

    private Product(
        Guid id,
        Guid tenantId,
        ProductContent content,
        IReadOnlyList<Guid> categoryTreeIds,
        IReadOnlyList<MediaReference> media,
        ProductStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? publishedAt,
        DateTimeOffset? archivedAt,
        string createdBy,
        string updatedBy,
        long version,
        ProductCreation creation)
    {
        Id = id;
        TenantId = tenantId;
        Content = content;
        CategoryTreeIds = categoryTreeIds;
        Media = media;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        PublishedAt = publishedAt;
        ArchivedAt = archivedAt;
        CreatedBy = createdBy;
        UpdatedBy = updatedBy;
        Version = version;
        Creation = creation;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public ProductContent Content { get; private set; }

    /// <summary>The product's categories and all their ancestors: a category filter matches descendants too.</summary>
    public IReadOnlyList<Guid> CategoryTreeIds { get; private set; }

    public IReadOnlyList<MediaReference> Media { get; private set; }

    public ProductStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>When the product was last published; kept while it is unpublished or archived.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public string CreatedBy { get; }

    public string UpdatedBy { get; private set; }

    public long Version { get; private set; }

    public ProductCreation Creation { get; }

    /// <summary>A new draft product of <paramref name="tenantId"/>.</summary>
    public static Product Create(
        Guid id,
        Guid tenantId,
        ProductContent content,
        IReadOnlyList<Guid> categoryTreeIds,
        ProductCreation creation,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(categoryTreeIds);
        ArgumentNullException.ThrowIfNull(creation);
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A product needs an ID and a tenant.");
        }

        return new Product(id, tenantId, content, categoryTreeIds, [], ProductStatus.Draft, now, now, null, null, creation.SubjectId, creation.SubjectId, 1, creation);
    }

    /// <summary>Restores a stored product, which satisfied every rule when it was written.</summary>
    public static Product Rehydrate(
        Guid id,
        Guid tenantId,
        ProductContent content,
        IReadOnlyList<Guid> categoryTreeIds,
        IReadOnlyList<MediaReference> media,
        ProductStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? publishedAt,
        DateTimeOffset? archivedAt,
        string createdBy,
        string updatedBy,
        long version,
        ProductCreation creation) =>
        new(id, tenantId, content, categoryTreeIds, media, status, createdAt, updatedAt, publishedAt, archivedAt, createdBy, updatedBy, version, creation);

    /// <summary>
    /// The publication requirements that <paramref name="content"/> (default: the current content) would not meet,
    /// given the state of its references. Empty means publishable.
    /// </summary>
    public static IReadOnlyList<string> UnmetPublicationRequirements(ProductContent content, ReferenceState references)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(references);

        var unmet = new List<string>();
        if (content.CategoryIds.Count == 0)
        {
            unmet.Add(PublicationRequirement.CategoryRequired);
        }
        else if (!references.AllCategoriesActive)
        {
            unmet.Add(PublicationRequirement.CategoryInactive);
        }

        if (content.BrandId is not null && !references.BrandActive)
        {
            unmet.Add(PublicationRequirement.BrandInactive);
        }

        if (!content.Variants.Any(variant => variant.IsActive))
        {
            unmet.Add(PublicationRequirement.ActiveVariantRequired);
        }

        return unmet;
    }

    /// <summary>
    /// Replaces the editable content. A published product is revalidated: a change that would break a publication
    /// requirement is refused and nothing changes (ADR-024 section 5).
    /// </summary>
    public ProductChangeOutcome ReplaceContent(
        ProductContent content,
        IReadOnlyList<Guid> categoryTreeIds,
        ReferenceState references,
        string subjectId,
        DateTimeOffset now,
        out IReadOnlyList<string> unmet)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(categoryTreeIds);
        unmet = [];

        if (Status == ProductStatus.Archived)
        {
            return ProductChangeOutcome.Archived;
        }

        if (Status == ProductStatus.Published)
        {
            unmet = UnmetPublicationRequirements(content, references);
            if (unmet.Count > 0)
            {
                return ProductChangeOutcome.RequirementsNotMet;
            }
        }

        Content = content;
        CategoryTreeIds = categoryTreeIds;
        Touch(subjectId, now);
        return ProductChangeOutcome.Changed;
    }

    /// <summary>Replaces the ordered media references. Media never affects publication.</summary>
    public ProductChangeOutcome ReplaceMedia(IReadOnlyList<MediaReference> media, string subjectId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (Status == ProductStatus.Archived)
        {
            return ProductChangeOutcome.Archived;
        }

        Media = media;
        Touch(subjectId, now);
        return ProductChangeOutcome.Changed;
    }

    /// <summary>Draft to Published, if every publication requirement is met.</summary>
    public TransitionOutcome Publish(ReferenceState references, string subjectId, DateTimeOffset now, out IReadOnlyList<string> unmet)
    {
        unmet = [];
        switch (Status)
        {
            case ProductStatus.Published:
                return TransitionOutcome.Unchanged;
            case ProductStatus.Archived:
                return TransitionOutcome.InvalidTransition;
        }

        unmet = UnmetPublicationRequirements(Content, references);
        if (unmet.Count > 0)
        {
            return TransitionOutcome.RequirementsNotMet;
        }

        Status = ProductStatus.Published;
        PublishedAt = now;
        Touch(subjectId, now);
        return TransitionOutcome.Changed;
    }

    /// <summary>Published back to Draft.</summary>
    public TransitionOutcome Unpublish(string subjectId, DateTimeOffset now) =>
        Status switch
        {
            ProductStatus.Draft => TransitionOutcome.Unchanged,
            ProductStatus.Published => Move(ProductStatus.Draft, subjectId, now),
            _ => TransitionOutcome.InvalidTransition,
        };

    /// <summary>Draft or Published to Archived: hidden from public discovery, read-only.</summary>
    public TransitionOutcome Archive(string subjectId, DateTimeOffset now)
    {
        if (Status == ProductStatus.Archived)
        {
            return TransitionOutcome.Unchanged;
        }

        ArchivedAt = now;
        return Move(ProductStatus.Archived, subjectId, now);
    }

    /// <summary>Archived back to Draft.</summary>
    public TransitionOutcome Restore(string subjectId, DateTimeOffset now)
    {
        switch (Status)
        {
            case ProductStatus.Draft:
                return TransitionOutcome.Unchanged;
            case ProductStatus.Published:
                return TransitionOutcome.InvalidTransition;
        }

        ArchivedAt = null;
        return Move(ProductStatus.Draft, subjectId, now);
    }

    /// <summary>Called by the store after a successful write: the version the stored document now has.</summary>
    public void AcceptStoredVersion(long version)
    {
        if (version <= Version)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A write always advances the stored version.");
        }

        Version = version;
    }

    /// <summary>Validates and builds the ordered media list, keeping the IDs of existing references.</summary>
    public static bool TryCreateMedia(
        IReadOnlyList<MediaInput>? input,
        IReadOnlyList<MediaReference> existing,
        Func<Guid> newId,
        [NotNullWhen(true)] out IReadOnlyList<MediaReference>? media,
        out ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(newId);

        errors = new ValidationErrors();
        var entries = input ?? [];
        var result = new List<MediaReference>(entries.Count);
        if (entries.Count > MaxMedia)
        {
            errors.Add("media", $"A product has at most {MaxMedia} media references.");
            media = null;
            return false;
        }

        var existingIds = existing.Select(reference => reference.Id).ToHashSet();
        var seenIds = new HashSet<Guid>();
        for (var index = 0; index < entries.Count; index++)
        {
            var field = $"media[{index}]";
            var entry = entries[index];
            if (entry is null)
            {
                errors.Add(field, "The media reference is required.");
                continue;
            }

            if (entry.Id is { } id && (!existingIds.Contains(id) || !seenIds.Add(id)))
            {
                errors.Add($"{field}.id", "The media ID is not a reference of this product, or is listed more than once.");
                continue;
            }

            if (MediaReference.TryCreate(entry.Id ?? newId(), entry.Url, entry.AltText, entry.ContentType, out var reference, out var entryErrors, $"{field}."))
            {
                result.Add(reference);
            }
            else
            {
                foreach (var (entryField, messages) in entryErrors.ToDictionary())
                {
                    foreach (var message in messages)
                    {
                        errors.Add(entryField, message);
                    }
                }
            }
        }

        media = errors.IsEmpty ? result : null;
        return media is not null;
    }

    private TransitionOutcome Move(ProductStatus status, string subjectId, DateTimeOffset now)
    {
        Status = status;
        Touch(subjectId, now);
        return TransitionOutcome.Changed;
    }

    private void Touch(string subjectId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        UpdatedBy = subjectId;
        UpdatedAt = now;
    }
}
