namespace EnterpriseCommerce.Inventory.Domain.Common;

/// <summary>
/// The quantity rules of ADR-026 section 1: whole units as <c>int</c>, within bounds that keep every sum inside the type
/// and refuse absurd input. Catalog has no unit of measure, so a fractional quantity is never valid.
/// </summary>
public static class Quantities
{
    /// <summary>The largest on-hand stock of one item.</summary>
    public const int MaxOnHand = 1_000_000_000;

    /// <summary>The largest change of one adjustment, in either direction.</summary>
    public const int MaxAdjustment = 1_000_000_000;

    /// <summary>The largest quantity of one reservation line.</summary>
    public const int MaxLineQuantity = 1_000_000;

    public static bool IsValidInitialOnHand(int quantity) => quantity is >= 0 and <= MaxOnHand;

    public static bool IsValidAdjustment(int delta) => delta != 0 && delta is >= -MaxAdjustment and <= MaxAdjustment;

    public static bool IsValidLineQuantity(int quantity) => quantity is >= 1 and <= MaxLineQuantity;
}
