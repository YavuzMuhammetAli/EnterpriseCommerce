using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

// Persistence documents (ADR-024 section 1): explicit classes mapped by hand to and from the domain, so the domain
// carries no BSON attributes and the stored shape is reviewed here. Element names are camelCase (MongoSerialization);
// GUIDs use the standard binary representation, money is Decimal128 and times are UTC BSON dates.

internal sealed class ProductDocument
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? ShortDescription { get; set; }

    public string? Description { get; set; }

    public Guid? BrandId { get; set; }

    public List<Guid> CategoryIds { get; set; } = [];

    /// <summary>The categories and their ancestors, for category filters that include descendants.</summary>
    public List<Guid> CategoryTreeIds { get; set; } = [];

    public List<AttributeDocument> Attributes { get; set; } = [];

    public List<VariantDocument> Variants { get; set; } = [];

    public List<MediaDocument> Media { get; set; } = [];

    public string Status { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? PublishedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime? ArchivedAt { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public string UpdatedBy { get; set; } = string.Empty;

    public long Version { get; set; }

    public CreationDocument Creation { get; set; } = new();
}

internal sealed class AttributeDocument
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    /// <summary>The lower-case comparison key of the value, matched by attribute filters.</summary>
    public string ValueKey { get; set; } = string.Empty;
}

internal sealed class VariantDocument
{
    public Guid Id { get; set; }

    public string Sku { get; set; } = string.Empty;

    /// <summary>The upper-case comparison key of the SKU, unique per tenant (index <c>ux_products_tenant_sku</c>).</summary>
    public string SkuKey { get; set; } = string.Empty;

    public List<AttributeDocument> Options { get; set; } = [];

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public bool IsActive { get; set; }
}

internal sealed class MediaDocument
{
    public Guid Id { get; set; }

    public string Url { get; set; } = string.Empty;

    public string? AltText { get; set; }

    public string ContentType { get; set; } = string.Empty;
}

internal sealed class CreationDocument
{
    public string SubjectId { get; set; } = string.Empty;

    public string IdempotencyKey { get; set; } = string.Empty;

    public string RequestHash { get; set; } = string.Empty;
}

internal sealed class CategoryDocument
{
    [BsonId]
    public Guid Id { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public Guid? ParentId { get; set; }

    public List<Guid> AncestorIds { get; set; } = [];

    public string Status { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }

    public long Version { get; set; }
}

internal sealed class BrandDocument
{
    [BsonId]
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Status { get; set; } = string.Empty;

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime CreatedAt { get; set; }

    [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
    public DateTime UpdatedAt { get; set; }

    public long Version { get; set; }
}
