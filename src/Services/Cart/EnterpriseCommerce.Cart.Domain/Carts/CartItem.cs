using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.Cart.Domain.Carts;

/// <summary>Whether a line can be checked out as far as the cart knows (ADR-029 section 6).</summary>
public enum ItemAvailability
{
    /// <summary>The variant was published and active at the last Catalog read, in the cart's currency.</summary>
    Available,

    /// <summary>At the last refresh the product was no longer published, or the variant was gone or inactive.</summary>
    Unavailable,

    /// <summary>At the last refresh the line's currency differed from the cart's.</summary>
    CurrencyMismatch,
}

/// <summary>
/// One line of a cart: a variant, its quantity and the snapshot of its product taken at the last Catalog read. Created
/// and changed only through <see cref="ShoppingCart"/>, which keeps the cart's rules.
/// </summary>
public sealed class CartItem
{
    private List<VariantOption> _options = [];

    internal CartItem(Guid id, ProductSnapshot snapshot, int quantity, DateTimeOffset now)
    {
        Id = id;
        Quantity = quantity;
        AddedAt = now;
        ApplySnapshot(snapshot, ItemAvailability.Available, now);
    }

    /// <summary>For the persistence layer only.</summary>
    private CartItem()
    {
        Name = string.Empty;
        Sku = string.Empty;
        Currency = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid VariantId { get; private set; }

    /// <summary>The product's tenant (the merchant). Equal to the cart's merchant in the single-merchant MVP.</summary>
    public Guid TenantId { get; private set; }

    public int Quantity { get; private set; }

    public string Name { get; private set; }

    public string Sku { get; private set; }

    public IReadOnlyList<VariantOption> Options => _options;

    public string? ImageUrl { get; private set; }

    public string? ImageAltText { get; private set; }

    public decimal UnitPrice { get; private set; }

    public string Currency { get; private set; }

    public ItemAvailability Availability { get; private set; }

    /// <summary>When Catalog last confirmed the price: the snapshot's age, never a guarantee.</summary>
    public DateTimeOffset PriceCheckedAt { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Money Price => Money.Create(UnitPrice, Currency);

    internal void SetQuantity(int quantity, DateTimeOffset now)
    {
        Quantity = quantity;
        UpdatedAt = now;
    }

    [MemberNotNull(nameof(Name), nameof(Sku), nameof(Currency))]
    internal void ApplySnapshot(ProductSnapshot snapshot, ItemAvailability availability, DateTimeOffset now)
    {
        ProductId = snapshot.ProductId;
        VariantId = snapshot.VariantId;
        TenantId = snapshot.TenantId;
        Name = snapshot.Name;
        Sku = snapshot.Sku;
        _options = [.. snapshot.Options];
        ImageUrl = snapshot.Image?.Url;
        ImageAltText = snapshot.Image?.AltText;
        UnitPrice = snapshot.UnitPrice.Amount;
        Currency = snapshot.UnitPrice.Currency;
        Availability = availability;
        PriceCheckedAt = now;
        UpdatedAt = now;
    }

    internal void MarkUnavailable(DateTimeOffset now)
    {
        Availability = ItemAvailability.Unavailable;
        UpdatedAt = now;
    }
}
