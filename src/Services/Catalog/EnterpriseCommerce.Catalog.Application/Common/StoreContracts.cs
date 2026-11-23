namespace EnterpriseCommerce.Catalog.Application.Common;

/// <summary>
/// The outcome of a single-document write (ADR-024 section 11). Uniqueness is enforced by the store's unique indexes,
/// so a concurrent duplicate is reported here even when an earlier check passed.
/// </summary>
public enum WriteOutcome
{
    Written,

    /// <summary>The document changed since it was read (optimistic concurrency); nothing was written.</summary>
    VersionConflict,

    /// <summary>The slug is taken within its scope (the tenant, or the platform for categories).</summary>
    SlugTaken,

    /// <summary>A SKU is taken by another product of the tenant.</summary>
    SkuTaken,

    /// <summary>The creating user already used the <c>Idempotency-Key</c> in this tenant.</summary>
    CreationKeyTaken,
}

/// <summary>The sort orders a client may choose (ADR-024 section 9). No other field or expression is accepted.</summary>
public enum CatalogSort
{
    /// <summary>Newest first: by publication time for public discovery, by update time for merchants.</summary>
    Newest,

    /// <summary>Oldest first.</summary>
    Oldest,
}
