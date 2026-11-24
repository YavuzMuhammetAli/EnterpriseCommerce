using EnterpriseCommerce.Cart.Domain.Carts;
using static EnterpriseCommerce.Cart.UnitTests.Domain.Snapshots;

namespace EnterpriseCommerce.Cart.UnitTests.Domain;

public sealed class RefreshTests
{
    private static readonly DateTimeOffset Later = Now.AddHours(2);

    [Fact]
    public void APriceChange_IsApplied_AndReported_WithThePreviousPrice()
    {
        var cart = NewCart();
        var snapshot = Of(amount: 19.99m);
        cart.AddItem(Guid.CreateVersion7(), snapshot, 2, Now);
        var current = Of(variantId: snapshot.VariantId, productId: snapshot.ProductId, amount: 17.99m);

        var changes = cart.Refresh(new HashSet<Guid> { snapshot.ProductId }, Variants(current), Later);

        var change = Assert.Single(changes);
        Assert.Equal(CartChangeKind.PriceChanged, change.Kind);
        Assert.Equal(19.99m, change.PreviousUnitPrice!.Amount);
        Assert.Equal(17.99m, change.UnitPrice!.Amount);
        Assert.Equal(17.99m, cart.Items[0].UnitPrice);
        Assert.Equal(2, cart.Items[0].Quantity);
        Assert.Equal(Later, cart.Items[0].PriceCheckedAt);
        Assert.Equal(2, cart.Version);
    }

    [Fact]
    public void AnUnchangedPrice_ReportsNothing_ButRecordsTheCheck()
    {
        var cart = NewCart();
        var snapshot = Of();
        cart.AddItem(Guid.CreateVersion7(), snapshot, 1, Now);

        var changes = cart.Refresh(new HashSet<Guid> { snapshot.ProductId }, Variants(snapshot), Later);

        Assert.Empty(changes);
        Assert.Equal(Later, cart.Items[0].PriceCheckedAt);
    }

    [Fact]
    public void AVariantThatIsGone_BecomesUnavailable_AndIsNeverRemoved()
    {
        var cart = NewCart();
        var snapshot = Of(amount: 5m);
        cart.AddItem(Guid.CreateVersion7(), snapshot, 3, Now);

        var changes = cart.Refresh(new HashSet<Guid> { snapshot.ProductId }, Variants(), Later);

        Assert.Equal(CartChangeKind.BecameUnavailable, Assert.Single(changes).Kind);
        var item = Assert.Single(cart.Items);
        Assert.Equal(ItemAvailability.Unavailable, item.Availability);
        Assert.Equal(5m, item.UnitPrice);
        Assert.Equal(3, item.Quantity);

        // Still gone: no second report.
        Assert.Empty(cart.Refresh(new HashSet<Guid> { snapshot.ProductId }, Variants(), Later));
    }

    [Fact]
    public void AVariantThatReturns_BecomesAvailable()
    {
        var cart = NewCart();
        var snapshot = Of();
        cart.AddItem(Guid.CreateVersion7(), snapshot, 1, Now);
        cart.Refresh(new HashSet<Guid> { snapshot.ProductId }, Variants(), Later);

        var changes = cart.Refresh(new HashSet<Guid> { snapshot.ProductId }, Variants(snapshot), Later.AddMinutes(1));

        Assert.Equal(CartChangeKind.BecameAvailable, Assert.Single(changes).Kind);
        Assert.Equal(ItemAvailability.Available, cart.Items[0].Availability);
    }

    [Fact]
    public void AnAnswerForAnotherProductOrMerchant_IsNotTrusted_AndTheLineBecomesUnavailable()
    {
        var cart = NewCart();
        var snapshot = Of();
        cart.AddItem(Guid.CreateVersion7(), snapshot, 1, Now);
        var otherMerchant = Of(variantId: snapshot.VariantId, productId: snapshot.ProductId, tenantId: MerchantB);

        cart.Refresh(new HashSet<Guid> { snapshot.ProductId }, Variants(otherMerchant), Later);

        Assert.Equal(ItemAvailability.Unavailable, cart.Items[0].Availability);
        Assert.Equal(MerchantA, cart.Items[0].TenantId);
    }

    [Fact]
    public void WhenEveryLineChangesCurrency_TheCartFollows()
    {
        var cart = NewCart();
        var snapshot = Of(currency: "EUR", amount: 10m);
        cart.AddItem(Guid.CreateVersion7(), snapshot, 1, Now);
        var inUsd = Of(variantId: snapshot.VariantId, productId: snapshot.ProductId, currency: "USD", amount: 11m);

        var changes = cart.Refresh(new HashSet<Guid> { snapshot.ProductId }, Variants(inUsd), Later);

        Assert.Equal(CartChangeKind.CurrencyChanged, Assert.Single(changes).Kind);
        Assert.Equal("USD", cart.Currency);
        Assert.Equal(ItemAvailability.Available, cart.Items[0].Availability);
    }

    [Fact]
    public void WhenLinesDisagreeOnCurrency_TheCartKeepsItsCurrency_AndTheOthersMismatch()
    {
        var cart = NewCart();
        var first = Of(currency: "EUR");
        var second = Of(currency: "EUR");
        cart.AddItem(Guid.CreateVersion7(), first, 1, Now);
        cart.AddItem(Guid.CreateVersion7(), second, 1, Now);
        var secondInUsd = Of(variantId: second.VariantId, productId: second.ProductId, currency: "USD");

        var changes = cart.Refresh(new HashSet<Guid> { first.ProductId, second.ProductId }, Variants(first, secondInUsd), Later);

        Assert.Equal("EUR", cart.Currency);
        Assert.Equal(ItemAvailability.Available, cart.Items.Single(item => item.VariantId == first.VariantId).Availability);
        Assert.Equal(ItemAvailability.CurrencyMismatch, cart.Items.Single(item => item.VariantId == second.VariantId).Availability);
        Assert.Contains(changes, change => change.Kind == CartChangeKind.CurrencyChanged);
    }

    [Fact]
    public void ALineOfAProductThatWasNotRead_IsLeftAsItIs()
    {
        var cart = NewCart();
        var read = Of(amount: 10m);
        var notRead = Of(amount: 20m);
        cart.AddItem(Guid.CreateVersion7(), read, 1, Now);
        cart.AddItem(Guid.CreateVersion7(), notRead, 1, Now);

        cart.Refresh(new HashSet<Guid> { read.ProductId }, Variants(Of(variantId: read.VariantId, productId: read.ProductId, amount: 8m)), Later);

        var untouched = cart.Items.Single(item => item.VariantId == notRead.VariantId);
        Assert.Equal(Now, untouched.PriceCheckedAt);
        Assert.Equal(ItemAvailability.Available, untouched.Availability);
    }

    [Fact]
    public void RefreshingAnEmptyCart_ChangesNothing()
    {
        var cart = NewCart();

        Assert.Empty(cart.Refresh(new HashSet<Guid>(), Variants(), Later));
        Assert.Equal(0, cart.Version);
    }

    private static Dictionary<Guid, ProductSnapshot> Variants(params ProductSnapshot[] snapshots) =>
        snapshots.ToDictionary(snapshot => snapshot.VariantId);
}
