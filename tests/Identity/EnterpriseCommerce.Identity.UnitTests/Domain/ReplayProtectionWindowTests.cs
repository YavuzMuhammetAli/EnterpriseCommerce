using EnterpriseCommerce.Identity.Domain.Security;

namespace EnterpriseCommerce.Identity.UnitTests.Domain;

public sealed class ReplayProtectionWindowTests
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    [Fact]
    public void TheWindow_IsTheLongestLifetimePlusTheMargin()
    {
        var window = ReplayProtectionWindow.Compute([TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10), TimeSpan.FromDays(7), TimeSpan.FromDays(30)], Day);

        Assert.Equal(TimeSpan.FromDays(31), window);
    }

    [Fact]
    public void AnOperatorFloor_OnlyLengthensTheWindow()
    {
        Assert.Equal(TimeSpan.FromDays(60), ReplayProtectionWindow.Compute([TimeSpan.FromDays(30)], Day, minimumWindow: TimeSpan.FromDays(60)));
        Assert.Equal(TimeSpan.FromDays(31), ReplayProtectionWindow.Compute([TimeSpan.FromDays(30)], Day, minimumWindow: TimeSpan.FromDays(2)));
    }

    [Fact]
    public void ATooSmallMargin_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplayProtectionWindow.Compute([Day], TimeSpan.FromMinutes(59)));

    [Fact]
    public void MissingOrNonPositiveLifetimes_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => ReplayProtectionWindow.Compute([], Day));
        Assert.Throws<ArgumentException>(() => ReplayProtectionWindow.Compute([Day, TimeSpan.Zero], Day));
    }

    [Fact]
    public void AnAbsurdWindow_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplayProtectionWindow.Compute([TimeSpan.FromDays(5000)], Day));
}
