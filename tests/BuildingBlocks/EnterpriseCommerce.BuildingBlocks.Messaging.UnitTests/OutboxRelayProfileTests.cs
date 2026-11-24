using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.BuildingBlocks.Messaging.UnitTests;

/// <summary>
/// What the shared block adds to the Phase 3 S4 relay (ADR-028 section 4): the per-service profile, the per-service
/// meter and the registration. The relay's behaviour itself is covered by Tenant &amp; Merchant's and Inventory's tests.
/// </summary>
public sealed class OutboxRelayProfileTests
{
    private static OutboxRelayProfile Profile(string service = "sample") => new()
    {
        OutboxSection = "Sample:Outbox",
        KafkaSection = "Sample:Kafka",
        DefaultTopic = $"{service}.events",
        KafkaClientId = $"{service}-outbox-relay",
        LockApplicationName = $"{service}-outbox-relay",
        MeterName = $"EnterpriseCommerce.Tests.{service}.Outbox",
        SettingsReference = "the sample runbook",
    };

    [Fact]
    public void AWellFormedProfile_IsValid()
    {
        Assert.Empty(Profile().Validate());
    }

    [Theory]
    [InlineData("topic", "Sample_Events")]
    [InlineData("topic", "")]
    [InlineData("client", "Sample Relay")]
    [InlineData("sections", "same")]
    [InlineData("meter", "")]
    public void AMalformedProfile_IsRefused(string field, string value)
    {
        var profile = field switch
        {
            "topic" => Profile() with { DefaultTopic = value },
            "client" => Profile() with { KafkaClientId = value },
            "sections" => Profile() with { OutboxSection = "Sample:Same", KafkaSection = "Sample:Same" },
            "meter" => Profile() with { MeterName = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.NotEmpty(profile.Validate());
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddSqlServerOutboxRelay<SampleContext>(new ConfigurationBuilder().Build(), profile, _ => "unused"));
    }

    [Fact]
    public void TheMeters_AreOnePerServiceName_AndShareNoInstrumentAcrossServices()
    {
        var first = OutboxMetrics.For("EnterpriseCommerce.Tests.First.Outbox");
        var again = OutboxMetrics.For("EnterpriseCommerce.Tests.First.Outbox");
        var second = OutboxMetrics.For("EnterpriseCommerce.Tests.Second.Outbox");

        Assert.Same(first, again);
        Assert.NotSame(first.Published, second.Published);
        Assert.Equal("EnterpriseCommerce.Tests.Second.Outbox", second.Published.Meter.Name);
    }

    [Fact]
    public void ThePendingGauge_ReportsTheObservedState_OfItsOwnService()
    {
        var metrics = OutboxMetrics.For("EnterpriseCommerce.Tests.Gauge.Outbox");
        var state = new OutboxRelayState(metrics);
        metrics.Observe(state);
        state.Update(status => status with { Backlog = new OutboxBacklog(7, DateTimeOffset.UtcNow) });

        long? pending = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == metrics.MeterName && instrument.Name == "outbox.pending")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => pending = value);
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.Equal(7, pending);
    }

    [Fact]
    public void TheRegistration_AppliesTheProfilesTopic_UnlessTheSectionOverridesIt()
    {
        Assert.Equal("sample.events", Resolve([]).Topic);
        Assert.Equal("sample.other", Resolve(new() { ["Sample:Outbox:Topic"] = "sample.other" }).Topic);
    }

    [Fact]
    public void TheRegistration_NamesTheProfilesSections_InItsValidationErrors()
    {
        var failure = Record.Exception(() => Build(new() { ["Sample:Outbox:BatchSize"] = "0" }, Environments.Development).GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("Sample:Outbox is invalid: see the sample runbook", failure?.ToString(), StringComparison.Ordinal);

        failure = Record.Exception(() => Build([], Environments.Production).GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains("Sample:Kafka is invalid", failure?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublisher_UsesTheProfilesClientId()
    {
        Assert.Equal("sample-outbox-relay", KafkaOutboxPublisher.ProducerConfig(new KafkaOptions { BootstrapServers = "broker:9092" }, Profile().KafkaClientId).ClientId);
    }

    private static OutboxOptions Resolve(Dictionary<string, string?> settings) =>
        Build(settings, Environments.Development).GetRequiredService<IOptions<OutboxOptions>>().Value;

    private static ServiceProvider Build(Dictionary<string, string?> settings, string environment)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment(environment));
        services.AddSqlServerOutboxRelay<SampleContext>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), Profile(), _ => "Server=unused");
        return services.BuildServiceProvider();
    }

    private sealed class SampleContext(DbContextOptions<SampleContext> options) : DbContext(options);

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "EnterpriseCommerce.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
