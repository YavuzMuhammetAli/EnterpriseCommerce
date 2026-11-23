using EnterpriseCommerce.Catalog.Domain.Common;
using EnterpriseCommerce.Catalog.Domain.Products;

namespace EnterpriseCommerce.Catalog.UnitTests.Domain;

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("red-shoes-2025")]
    [InlineData("a1b")]
    public void ValidSlugs_AreAccepted(string value) => Assert.True(Slug.TryCreate(value, out _));

    [Theory]
    [InlineData(null)]
    [InlineData("ab")]
    [InlineData("-abc")]
    [InlineData("abc-")]
    [InlineData("ab--c")]
    [InlineData("Abc")]
    [InlineData("ab c")]
    [InlineData("çay")]
    public void InvalidSlugs_AreRefusedNotNormalized(string? value) => Assert.False(Slug.TryCreate(value, out _));

    [Fact]
    public void ASlug_HasAtMost100Characters()
    {
        Assert.True(Slug.TryCreate(new string('a', 100), out _));
        Assert.False(Slug.TryCreate(new string('a', 101), out _));
    }

    [Theory]
    [InlineData("0.01", "EUR")]
    [InlineData("19.99", "USD")]
    [InlineData("1000000000", "TRY")]
    [InlineData("5", "GBP")]
    public void ValidPrices_AreAccepted(string amount, string currency) =>
        Assert.True(Money.TryCreate(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency, out _, out _, out _));

    [Theory]
    [InlineData("0", "EUR")]
    [InlineData("-1", "EUR")]
    [InlineData("1000000000.01", "EUR")]
    [InlineData("1.005", "EUR")]
    [InlineData("10", "eur")]
    [InlineData("10", "JPY")]
    public void InvalidPrices_AreRefused(string amount, string currency) =>
        Assert.False(Money.TryCreate(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency, out _, out _, out _));

    [Fact]
    public void APriceWithoutAmountOrCurrency_ReportsBoth()
    {
        Assert.False(Money.TryCreate(null, null, out _, out var amountError, out var currencyError));
        Assert.NotNull(amountError);
        Assert.NotNull(currencyError);
    }

    [Theory]
    [InlineData("SKU-1")]
    [InlineData("a.b_c-9")]
    public void ValidSkus_AreAccepted(string value) => Assert.True(Sku.TryCreate(value, out _));

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("ümlaut")]
    [InlineData("a/b")]
    public void InvalidSkus_AreRefused(string value) => Assert.False(Sku.TryCreate(value, out _));

    [Fact]
    public void Skus_CompareWithoutRegardToCase()
    {
        Assert.True(Sku.TryCreate("abc-1", out var lower));
        Assert.True(Sku.TryCreate("ABC-1", out var upper));
        Assert.Equal(lower.Key, upper.Key);
        Assert.Equal("abc-1", lower.Value);
    }

    [Fact]
    public void Attributes_TrimValues_AndCompareThemWithoutRegardToCase()
    {
        Assert.True(CatalogAttribute.TryCreate("color", "  Dark Red ", out var attribute, out _, out _));
        Assert.Equal("Dark Red", attribute.Value);
        Assert.Equal("dark red", attribute.ValueKey);
    }

    [Theory]
    [InlineData("Color", "red")]
    [InlineData("-color", "red")]
    [InlineData("color", "")]
    [InlineData("color", "a\nb")]
    [InlineData("a-very-long-attribute-key-of-more-than-forty", "x")]
    public void InvalidAttributes_AreRefused(string key, string value) =>
        Assert.False(CatalogAttribute.TryCreate(key, value, out _, out _, out _));

    [Theory]
    [InlineData("https://cdn.example.com/images/a.jpg")]
    [InlineData("https://images.shop.example/x.png?w=200")]
    public void PublicHttpsUrls_AreAccepted(string url) => Assert.True(MediaReference.IsAcceptableUrl(url));

    [Theory]
    [InlineData("http://cdn.example.com/a.jpg")]
    [InlineData("ftp://cdn.example.com/a.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,AAAA")]
    [InlineData("https://user:pass@cdn.example.com/a.jpg")]
    [InlineData("https://cdn.example.com/a.jpg#fragment")]
    [InlineData("https://127.0.0.1/a.jpg")]
    [InlineData("https://10.0.0.5/a.jpg")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/a.jpg")]
    [InlineData("https://2130706433/a.jpg")]
    [InlineData("https://0x7f.0.0.1/a.jpg")]
    [InlineData("https://localhost/a.jpg")]
    [InlineData("https://shop.localhost/a.jpg")]
    [InlineData("https://printer.local/a.jpg")]
    [InlineData("https://api.internal/a.jpg")]
    [InlineData("https://intranet/a.jpg")]
    [InlineData("/relative/a.jpg")]
    [InlineData("https://cdn.example.com/a b.jpg")]
    public void UnsafeOrNonPublicUrls_AreRefused(string url) => Assert.False(MediaReference.IsAcceptableUrl(url));

    [Fact]
    public void MediaReferences_RequireASupportedImageType()
    {
        Assert.True(MediaReference.TryCreate(Guid.NewGuid(), "https://cdn.example.com/a.webp", "Front", "image/webp", out _, out _));
        Assert.False(MediaReference.TryCreate(Guid.NewGuid(), "https://cdn.example.com/a.svg", null, "image/svg+xml", out _, out var errors));
        Assert.True(errors.Contains("contentType"));
    }
}
