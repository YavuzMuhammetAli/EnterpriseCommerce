using EnterpriseCommerce.Cart.Domain.Carts;
using static EnterpriseCommerce.Cart.UnitTests.Domain.Snapshots;

namespace EnterpriseCommerce.Cart.UnitTests.Domain;

public sealed class ShoppingCartTests
{
    [Fact]
    public void ANewCart_IsEmpty_WithoutMerchantOrCurrency_AndUnsaved()
    {
        var cart = NewCart();

        Assert.Empty(cart.Items);
        Assert.Null(cart.MerchantTenantId);
        Assert.Null(cart.Currency);
        Assert.Equal(0, cart.Version);
        Assert.Equal("customer-1", cart.CustomerSubject);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" customer")]
    [InlineData("customer\n")]
    public void ACart_NeedsAValidOwner(string subject) =>
        Assert.Throws<ArgumentException>(() => ShoppingCart.Create(Guid.CreateVersion7(), subject, Now));

    [Fact]
    public void ACart_NeedsAnOwnerOfAtMost200Characters()
    {
        Assert.NotNull(ShoppingCart.Create(Guid.CreateVersion7(), new string('s', 200), Now));
        Assert.Throws<ArgumentException>(() => ShoppingCart.Create(Guid.CreateVersion7(), new string('s', 201), Now));
    }

    [Fact]
    public void AddingAnItem_AddsALine_AndSetsMerchantAndCurrency()
    {
        var cart = NewCart();
        var snapshot = Of(amount: 10.50m);
        var itemId = Guid.CreateVersion7();

        Assert.Equal(CartOutcome.Added, cart.AddItem(itemId, snapshot, 2, Now.AddMinutes(1)));

        var item = Assert.Single(cart.Items);
        Assert.Equal(itemId, item.Id);
        Assert.Equal(snapshot.VariantId, item.VariantId);
        Assert.Equal(snapshot.ProductId, item.ProductId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(10.50m, item.UnitPrice);
        Assert.Equal("EUR", item.Currency);
        Assert.Equal(ItemAvailability.Available, item.Availability);
        Assert.Equal(Now.AddMinutes(1), item.PriceCheckedAt);
        Assert.Equal("https://cdn.example.com/a.jpg", item.ImageUrl);
        Assert.Equal(MerchantA, cart.MerchantTenantId);
        Assert.Equal("EUR", cart.Currency);
        Assert.Equal(1, cart.Version);
        Assert.Equal(Now.AddMinutes(1), cart.UpdatedAt);
    }

    [Fact]
    public void AddingTheSameVariantAgain_AddsToItsLine_AndTakesTheNewSnapshot()
    {
        var cart = NewCart();
        var first = Of(amount: 10m);
        cart.AddItem(Guid.CreateVersion7(), first, 2, Now);
        var again = Of(variantId: first.VariantId, productId: first.ProductId, amount: 9m, name: "Blue shirt (new)");

        Assert.Equal(CartOutcome.Incremented, cart.AddItem(Guid.CreateVersion7(), again, 3, Now.AddMinutes(5)));

        var item = Assert.Single(cart.Items);
        Assert.Equal(5, item.Quantity);
        Assert.Equal(9m, item.UnitPrice);
        Assert.Equal("Blue shirt (new)", item.Name);
        Assert.Equal(Now.AddMinutes(5), item.PriceCheckedAt);
        Assert.Equal(2, cart.Version);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(int.MaxValue)]
    public void AnInvalidQuantity_IsRefused_AndChangesNothing(int quantity)
    {
        var cart = NewCart();

        Assert.Equal(CartOutcome.InvalidQuantity, cart.AddItem(Guid.CreateVersion7(), Of(), quantity, Now));

        Assert.Empty(cart.Items);
        Assert.Equal(0, cart.Version);
    }

    [Fact]
    public void ALine_HoldsAtMost99Units()
    {
        var cart = NewCart();
        var snapshot = Of();
        Assert.Equal(CartOutcome.Added, cart.AddItem(Guid.CreateVersion7(), snapshot, 99, Now));

        Assert.Equal(CartOutcome.QuantityLimitExceeded, cart.AddItem(Guid.CreateVersion7(), snapshot, 1, Now));

        Assert.Equal(99, Assert.Single(cart.Items).Quantity);
        Assert.Equal(1, cart.Version);
    }

    [Fact]
    public void ACart_HoldsAtMost50Lines()
    {
        var cart = NewCart();
        for (var line = 0; line < CartLimits.MaxLines; line++)
        {
            Assert.Equal(CartOutcome.Added, cart.AddItem(Guid.CreateVersion7(), Of(), 1, Now));
        }

        Assert.Equal(CartOutcome.ItemLimitExceeded, cart.AddItem(Guid.CreateVersion7(), Of(), 1, Now));
        Assert.Equal(CartLimits.MaxLines, cart.Items.Count);

        // A variant already in the cart still adds to its line at the limit.
        Assert.Equal(CartOutcome.Incremented, cart.AddItem(Guid.CreateVersion7(), Of(variantId: cart.Items[0].VariantId, productId: cart.Items[0].ProductId), 1, Now));
    }

    [Fact]
    public void AnotherMerchantsProduct_IsRefused_AndTheCartIsUntouched()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), Of(tenantId: MerchantA), 1, Now);

        Assert.Equal(CartOutcome.MerchantMismatch, cart.AddItem(Guid.CreateVersion7(), Of(tenantId: MerchantB), 1, Now));

        Assert.Single(cart.Items);
        Assert.Equal(MerchantA, cart.MerchantTenantId);
        Assert.Equal(1, cart.Version);
    }

    [Fact]
    public void AnotherCurrency_IsRefused_AndTheCartIsUntouched()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), Of(currency: "EUR"), 1, Now);

        Assert.Equal(CartOutcome.CurrencyMismatch, cart.AddItem(Guid.CreateVersion7(), Of(currency: "USD"), 1, Now));

        Assert.Single(cart.Items);
        Assert.Equal("EUR", cart.Currency);
    }

    [Fact]
    public void SettingAQuantity_ReplacesIt_AndTheSameQuantityChangesNothing()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), Of(), 2, Now);
        var itemId = cart.Items[0].Id;

        Assert.Equal(CartOutcome.Changed, cart.SetQuantity(itemId, 7, Now));
        Assert.Equal(7, cart.Items[0].Quantity);
        Assert.Equal(2, cart.Version);

        Assert.Equal(CartOutcome.Unchanged, cart.SetQuantity(itemId, 7, Now));
        Assert.Equal(2, cart.Version);

        Assert.Equal(CartOutcome.InvalidQuantity, cart.SetQuantity(itemId, 0, Now));
        Assert.Equal(CartOutcome.InvalidQuantity, cart.SetQuantity(itemId, 100, Now));
        Assert.Equal(CartOutcome.ItemNotFound, cart.SetQuantity(Guid.CreateVersion7(), 1, Now));
        Assert.Equal(7, cart.Items[0].Quantity);
    }

    [Fact]
    public void RemovingTheLastLine_LeavesAValidEmptyCart_ThatAcceptsAnyMerchant()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), Of(tenantId: MerchantA), 1, Now);

        Assert.Equal(CartOutcome.Changed, cart.RemoveItem(cart.Items[0].Id, Now));

        Assert.Empty(cart.Items);
        Assert.Null(cart.MerchantTenantId);
        Assert.Null(cart.Currency);
        Assert.Equal(2, cart.Version);
        Assert.Equal(CartOutcome.Added, cart.AddItem(Guid.CreateVersion7(), Of(tenantId: MerchantB, currency: "USD"), 1, Now));
        Assert.Equal(MerchantB, cart.MerchantTenantId);
    }

    [Fact]
    public void RemovingAnUnknownLine_IsRefused()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), Of(), 1, Now);

        Assert.Equal(CartOutcome.ItemNotFound, cart.RemoveItem(Guid.CreateVersion7(), Now));
        Assert.Single(cart.Items);
    }

    [Fact]
    public void Clearing_RemovesEveryLine_AndClearingAnEmptyCartChangesNothing()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), Of(), 1, Now);
        cart.AddItem(Guid.CreateVersion7(), Of(), 1, Now);

        Assert.Equal(CartOutcome.Changed, cart.Clear(Now));
        Assert.Empty(cart.Items);
        Assert.Null(cart.MerchantTenantId);
        Assert.Equal(3, cart.Version);

        Assert.Equal(CartOutcome.Unchanged, cart.Clear(Now));
        Assert.Equal(3, cart.Version);
    }

    [Fact]
    public void ACart_IsExpired_AfterItsRetentionSinceTheLastChange()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), Of(), 1, Now);
        var retention = TimeSpan.FromDays(30);

        Assert.False(cart.IsExpired(Now.AddDays(30).AddTicks(-1), retention));
        Assert.True(cart.IsExpired(Now.AddDays(30), retention));
    }

    [Fact]
    public void StartingOverAfterExpiry_EmptiesTheCart_InTheSameRow()
    {
        var cart = NewCart();
        cart.AddItem(Guid.CreateVersion7(), Of(tenantId: MerchantA), 1, Now);
        var id = cart.Id;

        cart.StartOverAfterExpiry(Now.AddDays(40));

        Assert.Equal(id, cart.Id);
        Assert.Empty(cart.Items);
        Assert.Null(cart.MerchantTenantId);
        Assert.Equal(Now.AddDays(40), cart.CreatedAt);
        Assert.Equal(CartOutcome.Added, cart.AddItem(Guid.CreateVersion7(), Of(tenantId: MerchantB), 1, Now.AddDays(40)));
    }
}
