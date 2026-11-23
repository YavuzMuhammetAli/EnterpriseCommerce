using System.Diagnostics.CodeAnalysis;
using EnterpriseCommerce.Catalog.Domain.Common;

namespace EnterpriseCommerce.Catalog.Domain.Brands;

/// <summary>
/// A brand of one tenant (ADR-024 section 7). Products reference brands of their own tenant only. A brand is never
/// deleted; it is retired, so no product reference is ever left dangling. The tenant never changes.
/// </summary>
public sealed class Brand
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 500;

    private Brand(
        Guid id,
        Guid tenantId,
        Slug slug,
        string name,
        string? description,
        CatalogStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        Id = id;
        TenantId = tenantId;
        Slug = slug;
        Name = name;
        Description = description;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    public Slug Slug { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public CatalogStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    /// <summary>Validates the editable details of a brand.</summary>
    public static bool TryCreateDetails(
        string? slug,
        string? name,
        string? description,
        [NotNullWhen(true)] out BrandDetails? details,
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

        details = errors.IsEmpty ? new BrandDetails(validSlug!, validName, validDescription) : null;
        return details is not null;
    }

    /// <summary>A new active brand of <paramref name="tenantId"/>.</summary>
    public static Brand Create(Guid id, Guid tenantId, BrandDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (id == Guid.Empty || tenantId == Guid.Empty)
        {
            throw new ArgumentException("A brand needs an ID and a tenant.");
        }

        return new Brand(id, tenantId, details.Slug, details.Name, details.Description, CatalogStatus.Active, now, now, 1);
    }

    /// <summary>Restores a stored brand, which satisfied every rule when it was written.</summary>
    public static Brand Rehydrate(
        Guid id,
        Guid tenantId,
        Slug slug,
        string name,
        string? description,
        CatalogStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version) =>
        new(id, tenantId, slug, name, description, status, createdAt, updatedAt, version);

    /// <summary>Replaces the editable details. The tenant never changes.</summary>
    public void Update(BrandDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        Slug = details.Slug;
        Name = details.Name;
        Description = details.Description;
        UpdatedAt = now;
    }

    public TransitionOutcome Retire(DateTimeOffset now) => MoveTo(CatalogStatus.Retired, now);

    public TransitionOutcome Reactivate(DateTimeOffset now) => MoveTo(CatalogStatus.Active, now);

    /// <summary>Called by the store after a successful write.</summary>
    public void AcceptStoredVersion(long version)
    {
        if (version <= Version)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "A write always advances the stored version.");
        }

        Version = version;
    }

    private TransitionOutcome MoveTo(CatalogStatus status, DateTimeOffset now)
    {
        if (Status == status)
        {
            return TransitionOutcome.Unchanged;
        }

        Status = status;
        UpdatedAt = now;
        return TransitionOutcome.Changed;
    }
}

/// <summary>The validated, editable details of a brand.</summary>
public sealed record BrandDetails(Slug Slug, string Name, string? Description);
