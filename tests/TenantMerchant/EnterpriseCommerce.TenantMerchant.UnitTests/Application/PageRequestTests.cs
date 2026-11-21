using EnterpriseCommerce.TenantMerchant.Application.Common;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Application;

public sealed class PageRequestTests
{
    [Fact]
    public void Defaults_AreTheFirstPageOfTwenty()
    {
        Assert.True(PageRequest.TryCreate(null, null, out var page, out _));
        Assert.Equal(1, page.Page);
        Assert.Equal(20, page.PageSize);
        Assert.Equal(0, page.Skip);
    }

    [Fact]
    public void Skip_CountsThePrecedingPages()
    {
        Assert.True(PageRequest.TryCreate(3, 100, out var page, out _));
        Assert.Equal(200, page.Skip);
    }

    [Theory]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 101, "pageSize")]
    [InlineData(0, 20, "page")]
    [InlineData(-1, 20, "page")]
    [InlineData(int.MaxValue, 20, "page")]
    public void OutOfRangeValues_AreRejected(int page, int pageSize, string field)
    {
        Assert.False(PageRequest.TryCreate(page, pageSize, out _, out var errors));
        Assert.True(errors.ContainsKey(field));
    }
}
