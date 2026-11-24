using EnterpriseCommerce.Cart.Api.Security;
using EnterpriseCommerce.Cart.Application.Common;
using EnterpriseCommerce.Cart.Infrastructure.Caching;
using EnterpriseCommerce.Cart.Infrastructure.Cleanup;

namespace EnterpriseCommerce.Cart.UnitTests.Application;

public sealed class SettingsTests
{
    [Fact]
    public void TheDefaults_AreValid()
    {
        Assert.Empty(new CartLifecycleSettings().Validate());
        Assert.Empty(new CartPricingSettings().Validate());
        Assert.Empty(new CartCleanupOptions().Validate());
        Assert.Empty(new CartCacheOptions { ConnectionString = "127.0.0.1:6379" }.Validate());
        Assert.Empty(new CartAuthenticationSettings { Issuer = "https://identity.example/" }.Validate(isDevelopment: false));
    }

    [Fact]
    public void TheDefaultLifecycle_Is30Days_AndPricesAreStaleAfter15Minutes()
    {
        Assert.Equal(TimeSpan.FromDays(30), new CartLifecycleSettings().InactiveCartRetention);
        Assert.Equal(TimeSpan.FromMinutes(15), new CartPricingSettings().StaleAfter);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(366)]
    public void TheRetention_IsBounded(double days) =>
        Assert.NotEmpty(new CartLifecycleSettings { InactiveCartRetention = TimeSpan.FromDays(days) }.Validate());

    [Theory]
    [InlineData(0.5)]
    [InlineData(1441)]
    public void TheStalenessWindow_IsBounded(double minutes) =>
        Assert.NotEmpty(new CartPricingSettings { StaleAfter = TimeSpan.FromMinutes(minutes) }.Validate());

    [Fact]
    public void AnEnabledCache_NeedsAConnectionString_AndADisabledOneDoesNot()
    {
        Assert.NotEmpty(new CartCacheOptions { Enabled = true, ConnectionString = null }.Validate());
        Assert.Empty(new CartCacheOptions { Enabled = false, ConnectionString = null }.Validate());
    }

    public static TheoryData<string> InvalidCacheOptions() => ["ttl-short", "ttl-long", "timeout", "connect", "circuit", "queue", "prefix", "prefix-space", "repair"];

    [Theory]
    [MemberData(nameof(InvalidCacheOptions))]
    public void TheCacheBounds_AreEnforced(string option)
    {
        var options = new CartCacheOptions { ConnectionString = "127.0.0.1:6379" };
        switch (option)
        {
            case "ttl-short": options.TimeToLive = TimeSpan.FromSeconds(59); break;
            case "ttl-long": options.TimeToLive = TimeSpan.FromMinutes(61); break;
            case "timeout": options.OperationTimeout = TimeSpan.FromSeconds(3); break;
            case "connect": options.ConnectTimeout = TimeSpan.FromMilliseconds(10); break;
            case "circuit": options.CircuitBreakDuration = TimeSpan.Zero; break;
            case "queue": options.RepairQueueCapacity = -1; break;
            case "prefix": options.KeyPrefix = string.Empty; break;
            case "prefix-space": options.KeyPrefix = "ec cart:"; break;
            case "repair": options.RepairInterval = TimeSpan.FromMinutes(2); break;
            default: throw new ArgumentOutOfRangeException(nameof(option));
        }

        Assert.NotEmpty(options.Validate());
    }

    [Fact]
    public void TheCleanupBounds_AreEnforced()
    {
        Assert.NotEmpty(new CartCleanupOptions { Interval = TimeSpan.FromSeconds(1) }.Validate());
        Assert.NotEmpty(new CartCleanupOptions { BatchSize = 0 }.Validate());
        Assert.NotEmpty(new CartCleanupOptions { MaxBatchesPerCycle = 101 }.Validate());
    }

    [Fact]
    public void TheIssuer_MustBeHttpsOutsideDevelopment()
    {
        var settings = new CartAuthenticationSettings { Issuer = "http://localhost:7101/" };

        Assert.NotEmpty(settings.Validate(isDevelopment: false));
        Assert.Empty(settings.Validate(isDevelopment: true));
        Assert.NotEmpty(new CartAuthenticationSettings().Validate(isDevelopment: true));
    }

    [Fact]
    public void TheCustomerAudience_DefaultsToTheStorefront()
    {
        Assert.Equal(["ec-storefront"], new CartAuthenticationSettings().EffectiveUserAudiences);
        Assert.Equal(["ec-other"], new CartAuthenticationSettings { UserAudiences = ["ec-other"] }.EffectiveUserAudiences);
        Assert.NotEmpty(new CartAuthenticationSettings { Issuer = "https://identity.example/", UserAudiences = [" "] }.Validate(isDevelopment: false));
    }
}
