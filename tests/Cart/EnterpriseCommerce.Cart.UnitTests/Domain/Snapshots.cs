using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.UnitTests.Domain;

/// <summary>Valid domain values for tests.</summary>
internal static class Snapshots
{
    public static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    public static readonly Guid MerchantA = Guid.Parse("0193b6a0-0000-7000-8000-00000000000a");
    public static readonly Guid MerchantB = Guid.Parse("0193b6a0-0000-7000-8000-00000000000b");

    public static ProductSnapshot Of(
        Guid? variantId = null,
        Guid? productId = null,
        Guid? tenantId = null,
        decimal amount = 19.99m,
        string currency = "EUR",
        string name = "Blue shirt") =>
        ProductSnapshot.TryCreate(
            productId ?? Guid.CreateVersion7(),
            variantId ?? Guid.CreateVersion7(),
            tenantId ?? MerchantA,
            name,
            "BS-M",
            [new VariantOption("size", "M")],
            new ProductImage("https://cdn.example.com/a.jpg", "Front"),
            Money.Create(amount, currency))!;

    public static ShoppingCart NewCart(string subject = "customer-1") => ShoppingCart.Create(Guid.CreateVersion7(), subject, Now);
}
