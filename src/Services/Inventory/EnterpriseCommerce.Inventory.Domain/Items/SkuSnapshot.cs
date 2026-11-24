namespace EnterpriseCommerce.Inventory.Domain.Items;

/// <summary>
/// The SKU of a variant as Catalog reported it when the inventory item was created (ADR-026 sections 2 and 11): 1 to 64
/// characters of <c>A-Z a-z 0-9 . _ -</c>, Catalog's rule (ADR-024 section 3). Informational: Catalog owns the SKU, and
/// the snapshot can go stale after a Catalog change.
/// </summary>
public sealed record SkuSnapshot
{
    public const int MaxLength = 64;

    private SkuSnapshot(string value) => Value = value;

    public string Value { get; }

    public static SkuSnapshot? TryCreate(string? value) =>
        value is { Length: >= 1 and <= MaxLength } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')
            ? new SkuSnapshot(value)
            : null;

    public override string ToString() => Value;
}
