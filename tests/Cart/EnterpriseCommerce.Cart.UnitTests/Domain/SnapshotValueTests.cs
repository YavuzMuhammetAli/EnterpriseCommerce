using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.UnitTests.Domain;

public sealed class SnapshotValueTests
{
    [Theory]
    [InlineData("0.01", "EUR")]
    [InlineData("19.99", "TRY")]
    [InlineData("1000000000", "USD")]
    [InlineData("12.5", "GBP")]
    public void AValidPrice_IsAccepted(string amount, string currency) =>
        Assert.True(Money.TryCreate(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency, out _));

    [Theory]
    [InlineData("0", "EUR")]
    [InlineData("-1", "EUR")]
    [InlineData("1000000000.01", "EUR")]
    [InlineData("1.001", "EUR")]
    [InlineData("1", "eur")]
    [InlineData("1", "EU")]
    [InlineData("1", "EURO")]
    [InlineData("1", "E1R")]
    [InlineData("1", "")]
    public void AnInvalidPrice_IsRefused(string amount, string currency) =>
        Assert.False(Money.TryCreate(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency, out _));

    [Fact]
    public void ASnapshot_WithValidValues_IsCreated()
    {
        var snapshot = ProductSnapshot.TryCreate(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Shirt", "SKU-1", [], null, Money.Create(1m, "EUR"));

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.Image);
    }

    public static TheoryData<string> InvalidSnapshots() =>
    [
        "empty-product", "empty-variant", "empty-tenant", "no-name", "long-name", "control-name", "no-sku", "long-sku",
        "too-many-options", "long-option", "http-image", "relative-image", "long-alt", "no-price",
    ];

    [Theory]
    [MemberData(nameof(InvalidSnapshots))]
    public void ASnapshot_OutsideTheBounds_IsRefused(string variant)
    {
        var options = new List<VariantOption> { new("size", "M") };
        var image = new ProductImage("https://cdn.example.com/a.jpg", "Front");
        var price = Money.Create(1m, "EUR");
        var (product, variantId, tenant, name, sku) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Shirt", "SKU-1");

        switch (variant)
        {
            case "empty-product": product = Guid.Empty; break;
            case "empty-variant": variantId = Guid.Empty; break;
            case "empty-tenant": tenant = Guid.Empty; break;
            case "no-name": name = string.Empty; break;
            case "long-name": name = new string('n', ProductSnapshot.MaxNameLength + 1); break;
            case "control-name": name = "Shirt\u0000"; break;
            case "no-sku": sku = string.Empty; break;
            case "long-sku": sku = new string('s', ProductSnapshot.MaxSkuLength + 1); break;
            case "too-many-options": options = [.. Enumerable.Range(0, 11).Select(i => new VariantOption($"k{i}", "v"))]; break;
            case "long-option": options = [new VariantOption("size", new string('v', VariantOption.MaxValueLength + 1))]; break;
            case "http-image": image = new ProductImage("http://cdn.example.com/a.jpg", null); break;
            case "relative-image": image = new ProductImage("/a.jpg", null); break;
            case "long-alt": image = new ProductImage("https://cdn.example.com/a.jpg", new string('a', ProductImage.MaxAltTextLength + 1)); break;
            case "no-price": price = null!; break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }

        Assert.Null(ProductSnapshot.TryCreate(product, variantId, tenant, name, sku, options, image, price));
    }
}
