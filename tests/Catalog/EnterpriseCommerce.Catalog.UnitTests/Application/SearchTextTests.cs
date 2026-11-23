using EnterpriseCommerce.Catalog.Application.Common;

namespace EnterpriseCommerce.Catalog.UnitTests.Application;

public sealed class SearchTextTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("tea", "tea")]
    [InlineData("  green   tea  ", "green tea")]
    [InlineData("-cheap", "cheap")]
    [InlineData("\"exact phrase\"", "exact phrase")]
    [InlineData("t-shirt", "t shirt")]
    public void SearchTexts_AreNormalized_WithoutOperators(string? input, string? expected)
    {
        Assert.True(SearchText.TryCreate(input, out var text));
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("--")]
    [InlineData("tab\there")]
    [InlineData("one two three four five six seven eight nine ten eleven")]
    public void UnusableSearchTexts_AreRefused(string input) => Assert.False(SearchText.TryCreate(input, out _));

    [Fact]
    public void ASearchText_HasAtMost100Characters()
    {
        Assert.True(SearchText.TryCreate(new string('a', 100), out _));
        Assert.False(SearchText.TryCreate(new string('a', 101), out _));
    }
}
