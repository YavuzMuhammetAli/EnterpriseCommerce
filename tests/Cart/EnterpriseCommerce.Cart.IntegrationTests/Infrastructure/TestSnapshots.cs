using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

/// <summary>Valid product snapshots for persistence tests.</summary>
internal static class TestSnapshots
{
    public static ProductSnapshot Of(Guid merchant, decimal amount = 19.99m, string currency = "EUR", Guid? variantId = null, Guid? productId = null) =>
        ProductSnapshot.TryCreate(
            productId ?? Guid.CreateVersion7(),
            variantId ?? Guid.CreateVersion7(),
            merchant,
            "Blue shirt",
            "BS-M",
            [new VariantOption("size", "M")],
            new ProductImage("https://cdn.example.com/a.jpg", "Front"),
            Money.Create(amount, currency))!;
}
