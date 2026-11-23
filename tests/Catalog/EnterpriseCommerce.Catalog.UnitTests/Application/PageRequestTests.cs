using EnterpriseCommerce.Catalog.Application.Common;

namespace EnterpriseCommerce.Catalog.UnitTests.Application;

public sealed class PageRequestTests
{
    [Fact]
    public void Defaults_AreThePlatformConvention()
    {
        Assert.True(PageRequest.TryCreate(null, null, out var request, out _));
        Assert.Equal((1, 20, 0), (request.Page, request.PageSize, request.Skip));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(-1, 10)]
    public void OutOfRangeValues_AreRefused(int page, int pageSize)
    {
        Assert.False(PageRequest.TryCreate(page, pageSize, out _, out var errors));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void PublicDiscovery_StopsAtItsPageLimit()
    {
        Assert.True(PageRequest.TryCreate(PageRequest.MaxPublicPage, 100, PageRequest.MaxPublicPage, out var last, out _));
        Assert.Equal(9_900, last.Skip);
        Assert.False(PageRequest.TryCreate(PageRequest.MaxPublicPage + 1, 20, PageRequest.MaxPublicPage, out _, out var errors));
        Assert.True(errors.ContainsKey("page"));
    }
}
