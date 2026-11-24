using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.Application.Carts;

/// <summary>
/// A committed cart as reads return it: what is stored, without anything computed at read time (staleness, totals,
/// expiry). It is the value the cache holds (ADR-030 section 1).
/// </summary>
/// <param name="Stamp">
/// The cart row's SQL Server <c>rowversion</c> as 16 upper-case hexadecimal digits. It increases with every write in
/// the database, so two views of a customer's carts compare in commit order, also across a deleted and recreated cart.
/// </param>
public sealed record CartView(
    Guid Id,
    int Version,
    string Stamp,
    Guid? MerchantTenantId,
    string? Currency,
    IReadOnlyList<CartLineView> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static CartView From(ShoppingCart cart, string stamp)
    {
        ArgumentNullException.ThrowIfNull(cart);
        ArgumentException.ThrowIfNullOrEmpty(stamp);
        return new CartView(
            cart.Id,
            cart.Version,
            stamp,
            cart.MerchantTenantId,
            cart.Currency,
            [.. cart.Items.OrderBy(item => item.AddedAt).ThenBy(item => item.Id).Select(CartLineView.From)],
            cart.CreatedAt,
            cart.UpdatedAt);
    }
}

/// <summary>A stored cart line and its product snapshot.</summary>
public sealed record CartLineView(
    Guid Id,
    Guid ProductId,
    Guid VariantId,
    Guid TenantId,
    string Name,
    string Sku,
    IReadOnlyList<VariantOption> Options,
    string? ImageUrl,
    string? ImageAltText,
    int Quantity,
    decimal UnitPrice,
    string Currency,
    ItemAvailability Availability,
    DateTimeOffset PriceCheckedAt,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt)
{
    public static CartLineView From(CartItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new CartLineView(
            item.Id,
            item.ProductId,
            item.VariantId,
            item.TenantId,
            item.Name,
            item.Sku,
            [.. item.Options],
            item.ImageUrl,
            item.ImageAltText,
            item.Quantity,
            item.UnitPrice,
            item.Currency,
            item.Availability,
            item.PriceCheckedAt,
            item.AddedAt,
            item.UpdatedAt);
    }
}
