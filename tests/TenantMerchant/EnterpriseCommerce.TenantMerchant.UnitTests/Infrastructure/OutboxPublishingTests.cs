using System.Text;
using Confluent.Kafka;
using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>The Kafka record, the producer settings, the failure categories and the relay's configuration rules.</summary>
public sealed class OutboxPublishingTests
{
    [Fact]
    public void TheKafkaRecord_IsKeyedByTheTenant_CarriesTheStoredBytes_AndRepeatsTheRoutingHeaders()
    {
        var eventId = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7().ToString("D");
        var envelope = $$$"""{"eventId":"{{{eventId}}}","correlationId":"corr-42","payload":{"displayName":"Café"}}""";
        var row = new PendingOutboxMessage(7, eventId, "tenant-merchant.events", tenantId, "TenantCreated", 1, envelope, DateTimeOffset.UtcNow, 0, null);

        var record = KafkaOutboxPublisher.ToKafkaMessage(row);

        Assert.Equal(tenantId, record.Key);
        Assert.Equal(Encoding.UTF8.GetBytes(envelope), record.Value);
        Assert.Equal(
            [("event-id", eventId.ToString("D")), ("event-type", "TenantCreated"), ("event-version", "1"), ("correlation-id", "corr-42"), ("content-type", "application/json")],
            record.Headers.Select(header => (header.Key, Encoding.UTF8.GetString(header.GetValueBytes()))));
        Assert.Equal(record.Value, KafkaOutboxPublisher.ToKafkaMessage(row).Value);
    }

    [Fact]
    public void TheProducer_IsIdempotent_WaitsForAllReplicas_AndNeverCreatesTopics()
    {
        var config = KafkaOutboxPublisher.ProducerConfig(new KafkaOptions { BootstrapServers = "broker:9092", MessageTimeout = TimeSpan.FromSeconds(20) }, TenantMerchantOutbox.Profile.KafkaClientId);

        Assert.Equal(Acks.All, config.Acks);
        Assert.True(config.EnableIdempotence);
        Assert.Equal(20000, config.MessageTimeoutMs);
        Assert.False(config.AllowAutoCreateTopics);
        Assert.Equal("tenant-merchant-outbox-relay", config.ClientId);
        Assert.Equal(SecurityProtocol.Plaintext, config.SecurityProtocol);
    }

    [Fact]
    public void MutualTls_PassesTheCertificateFiles_ToTheClient()
    {
        var config = KafkaOutboxPublisher.ClientConfig(new KafkaOptions
        {
            BootstrapServers = "broker:9093",
            SecurityProtocol = KafkaSecurityProtocol.Ssl,
            SslCaLocation = "/run/secrets/ca.pem",
            SslCertificateLocation = "/run/secrets/client.pem",
            SslKeyLocation = "/run/secrets/client.key",
        }, TenantMerchantOutbox.Profile.KafkaClientId);

        Assert.Equal(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.Equal(("/run/secrets/ca.pem", "/run/secrets/client.pem", "/run/secrets/client.key"), (config.SslCaLocation, config.SslCertificateLocation, config.SslKeyLocation));
    }

    [Theory]
    [InlineData(ErrorCode.Local_MsgTimedOut, "Transient")]
    [InlineData(ErrorCode.Local_AllBrokersDown, "Transient")]
    [InlineData(ErrorCode.Local_Transport, "Transient")]
    [InlineData(ErrorCode.NotEnoughReplicas, "Transient")]
    [InlineData(ErrorCode.LeaderNotAvailable, "Transient")]
    [InlineData(ErrorCode.UnknownTopicOrPart, "Topic")]
    [InlineData(ErrorCode.Local_UnknownTopic, "Topic")]
    [InlineData(ErrorCode.TopicAuthorizationFailed, "Authorization")]
    [InlineData(ErrorCode.SaslAuthenticationFailed, "Authorization")]
    [InlineData(ErrorCode.Local_Ssl, "Authorization")]
    [InlineData(ErrorCode.MsgSizeTooLarge, "Rejected")]
    [InlineData(ErrorCode.PolicyViolation, "Rejected")]
    [InlineData(ErrorCode.Local_Fatal, "Configuration")]
    [InlineData(ErrorCode.Local_InvalidArg, "Configuration")]
    [InlineData(ErrorCode.Unknown, "Unknown")]
    public void BrokerErrors_MapToABoundedCategory(ErrorCode code, string expected)
    {
        Assert.Equal(Enum.Parse<PublishFailureCategory>(expected), KafkaOutboxPublisher.Categorize(code));
    }

    [Fact]
    public void OnlyTransientAndUnknownFailures_AreExpectedToHealByThemselves()
    {
        Assert.False(PublishResult.Failure(PublishFailureCategory.Transient, "x").IsPermanent);
        Assert.False(PublishResult.Failure(PublishFailureCategory.Unknown, "x").IsPermanent);
        Assert.True(PublishResult.Failure(PublishFailureCategory.Topic, "x").IsPermanent);
        Assert.False(PublishResult.Success.IsPermanent);
        Assert.Equal("Rejected:MsgSizeTooLarge", PublishResult.Failure(PublishFailureCategory.Rejected, "MsgSizeTooLarge").Describe());
    }

    [Fact]
    public void TheDefaultOutboxSettings_AreValid_AndMatchTheDesign()
    {
        var settings = TenantMerchantOutbox.DefaultOptions();

        Assert.Empty(settings.Validate());
        Assert.Equal(("tenant-merchant.events", TimeSpan.FromSeconds(1), 100, TimeSpan.FromSeconds(60)), (settings.Topic, settings.PollInterval, settings.BatchSize, settings.MaxBackoff));
        Assert.Equal((TimeSpan.FromDays(7), TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), 10), (settings.PublishedRetention, settings.CleanupInterval, settings.StaleAfter, settings.HaltAfterAttempts));
    }

    [Theory]
    [InlineData("Topic", "Tenant_Merchant.Events")]
    [InlineData("Topic", "")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "1001")]
    [InlineData("PollInterval", "00:00:00.010")]
    [InlineData("MaxBackoff", "00:00:00.500")]
    [InlineData("HaltAfterAttempts", "0")]
    [InlineData("PublishedRetention", "00:10:00")]
    [InlineData("StaleAfter", "00:00:01")]
    public void AnOutboxSettingOutOfRange_IsRefused(string setting, string value)
    {
        var settings = TenantMerchantOutbox.DefaultOptions();
        switch (setting)
        {
            case "Topic": settings.Topic = value; break;
            case "BatchSize": settings.BatchSize = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "HaltAfterAttempts": settings.HaltAfterAttempts = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "PollInterval": settings.PollInterval = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "MaxBackoff": settings.MaxBackoff = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "PublishedRetention": settings.PublishedRetention = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "StaleAfter": settings.StaleAfter = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            default: throw new ArgumentOutOfRangeException(nameof(setting));
        }

        Assert.NotEmpty(settings.Validate());
    }

    [Fact]
    public void InDevelopment_NoBroker_DisablesTheRelay_AndPlaintextIsAccepted()
    {
        Assert.Empty(new KafkaOptions().Validate(isDevelopment: true));
        Assert.False(new KafkaOptions().IsConfigured);
        Assert.Empty(new KafkaOptions { BootstrapServers = "127.0.0.1:9094" }.Validate(isDevelopment: true));
    }

    [Fact]
    public void OutsideDevelopment_ABrokerWithMutualTls_IsRequired()
    {
        Assert.NotEmpty(new KafkaOptions().Validate(isDevelopment: false));
        Assert.NotEmpty(new KafkaOptions { BootstrapServers = "broker:9092" }.Validate(isDevelopment: false));
        Assert.NotEmpty(new KafkaOptions { BootstrapServers = "broker:9093", SecurityProtocol = KafkaSecurityProtocol.Ssl }.Validate(isDevelopment: false));

        var certificate = Path.GetTempFileName();
        var key = Path.GetTempFileName();
        try
        {
            Assert.Empty(new KafkaOptions
            {
                BootstrapServers = "broker:9093",
                SecurityProtocol = KafkaSecurityProtocol.Ssl,
                SslCertificateLocation = certificate,
                SslKeyLocation = key,
            }.Validate(isDevelopment: false));
            Assert.NotEmpty(new KafkaOptions
            {
                BootstrapServers = "broker:9093",
                SecurityProtocol = KafkaSecurityProtocol.Ssl,
                SslCertificateLocation = certificate,
                SslKeyLocation = key + ".missing",
            }.Validate(isDevelopment: false));
        }
        finally
        {
            File.Delete(certificate);
            File.Delete(key);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void AMessageTimeoutOutOfRange_IsRefused(int seconds)
    {
        Assert.NotEmpty(new KafkaOptions { BootstrapServers = "127.0.0.1:9094", MessageTimeout = TimeSpan.FromSeconds(seconds) }.Validate(isDevelopment: true));
    }
}
