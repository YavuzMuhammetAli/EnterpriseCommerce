using EnterpriseCommerce.TenantMerchant.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>
/// The startup validation of the relay settings (ADR-023 section 6), through the real Infrastructure registration and
/// the same <see cref="IStartupValidator"/> the host runs before it starts: invalid settings stop the service.
/// </summary>
public sealed class OutboxStartupValidationTests
{
    [Fact]
    public void InDevelopment_WithoutABroker_TheServiceStarts_WithTheRelayDisabled()
    {
        Assert.Null(Validate(Environments.Development, []));
    }

    [Theory]
    [InlineData("TenantMerchant:Outbox:BatchSize", "0", "TenantMerchant:Outbox is invalid")]
    [InlineData("TenantMerchant:Outbox:Topic", "Tenant_Events", "TenantMerchant:Outbox is invalid")]
    [InlineData("TenantMerchant:Kafka:MessageTimeout", "00:10:00", "TenantMerchant:Kafka is invalid")]
    public void InvalidRelaySettings_StopTheService(string setting, string value, string expected)
    {
        var failure = Validate(Environments.Development, new() { ["TenantMerchant:Kafka:BootstrapServers"] = "127.0.0.1:9094", [setting] = value });

        Assert.NotNull(failure);
        Assert.Contains(expected, failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("broker:9092", null)]
    [InlineData("broker:9093", "Ssl")]
    public void OutsideDevelopment_TheServiceRefusesToStart_WithoutAMutualTlsBroker(string? bootstrapServers, string? protocol)
    {
        var settings = new Dictionary<string, string?>();
        if (bootstrapServers is not null)
        {
            settings["TenantMerchant:Kafka:BootstrapServers"] = bootstrapServers;
        }

        if (protocol is not null)
        {
            settings["TenantMerchant:Kafka:SecurityProtocol"] = protocol;
        }

        var failure = Validate(Environments.Production, settings);

        Assert.NotNull(failure);
        Assert.Contains("TenantMerchant:Kafka is invalid", failure.ToString(), StringComparison.Ordinal);
    }

    private static Exception? Validate(string environment, Dictionary<string, string?> settings)
    {
        settings["ConnectionStrings:TenantMerchantDatabase"] = "Server=unused;Database=unused";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddTenantMerchantInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        return Record.Exception(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "EnterpriseCommerce.TenantMerchant.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
