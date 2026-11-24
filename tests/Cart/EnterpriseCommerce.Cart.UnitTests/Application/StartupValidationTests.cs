using EnterpriseCommerce.Cart.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.Cart.UnitTests.Application;

/// <summary>
/// The startup rules (CART_OPERATIONS.md section 2): the host refuses to start with an invalid configuration. Tested
/// through the options' startup validator, without a web host (docs/testing TS-3).
/// </summary>
public sealed class StartupValidationTests
{
    private static readonly Dictionary<string, string?> Valid = new()
    {
        ["ConnectionStrings:CartDatabase"] = "Server=sql.internal;Database=EnterpriseCommerce_Cart",
        ["ConnectionStrings:CartCache"] = "cache.internal:6379",
        ["Cart:Catalog:BaseAddress"] = "https://catalog.internal/",
    };

    [Fact]
    public void AValidProductionConfiguration_Starts() => Validate(Valid, "Production");

    [Theory]
    [InlineData("ConnectionStrings:CartDatabase", null, "ConnectionStrings:CartDatabase")]
    [InlineData("ConnectionStrings:CartCache", null, "Cart:Cache")]
    [InlineData("Cart:Catalog:BaseAddress", null, "Cart:Catalog")]
    [InlineData("Cart:Catalog:BaseAddress", "http://catalog.internal/", "Cart:Catalog")]
    [InlineData("Cart:Cache:TimeToLive", "02:00:00", "Cart:Cache")]
    [InlineData("Cart:Lifecycle:InactiveCartRetention", "400.00:00:00", "Cart:Lifecycle")]
    [InlineData("Cart:Pricing:StaleAfter", "00:00:10", "Cart:Pricing")]
    [InlineData("Cart:Cleanup:BatchSize", "0", "Cart:Cleanup")]
    public void AnInvalidSetting_StopsTheHost(string key, string? value, string reported)
    {
        var settings = new Dictionary<string, string?>(Valid) { [key] = value };

        var error = Assert.ThrowsAny<OptionsValidationException>(() => Validate(settings, "Production"));

        Assert.Contains(reported, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InDevelopment_TheCatalogAddressMayBeMissing_AndTheCacheDisabled()
    {
        var settings = new Dictionary<string, string?>(Valid)
        {
            ["Cart:Catalog:BaseAddress"] = null,
            ["ConnectionStrings:CartCache"] = null,
            ["Cart:Cache:Enabled"] = "false",
        };

        Validate(settings, "Development");
    }

    private static void Validate(Dictionary<string, string?> settings, string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new Environment(environment));
        services.AddCartInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "EnterpriseCommerce.Cart.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
