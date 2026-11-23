using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

/// <summary>
/// Publishes outbox rows to Kafka with Confluent.Kafka (ADR-023 sections 1, 4 and 5): the key is the row's
/// <c>MessageKey</c> (the tenant ID), the value is the stored envelope's UTF-8 bytes, and the headers repeat the routing
/// values. The producer is idempotent and waits for every in-sync replica (<c>acks=all</c>); each call awaits the
/// delivery report, so the relay publishes strictly one message at a time. It never creates a topic.
/// </summary>
internal sealed partial class KafkaOutboxPublisher(IOptions<KafkaOptions> options, ILogger<KafkaOutboxPublisher> logger) : IOutboxPublisher
{
    /// <summary>The producer's <c>client.id</c>.</summary>
    public const string ClientId = "tenant-merchant-outbox-relay";

    private readonly Lock _gate = new();
    private IProducer<string, byte[]>? _producer;

    public async Task<PublishResult> PublishAsync(PendingOutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        IProducer<string, byte[]> producer;
        try
        {
            producer = Producer();
        }
        catch (KafkaException exception)
        {
            return PublishResult.Failure(PublishFailureCategory.Configuration, exception.Error.Code.ToString());
        }
        catch (ArgumentException)
        {
            return PublishResult.Failure(PublishFailureCategory.Configuration, "InvalidClientConfiguration");
        }

        try
        {
            await producer.ProduceAsync(message.Topic, ToKafkaMessage(message), cancellationToken);
            return PublishResult.Success;
        }
        catch (ProduceException<string, byte[]> exception)
        {
            DiscardIfFatal(exception.Error);
            return PublishResult.Failure(Categorize(exception.Error.Code), exception.Error.Code.ToString());
        }
        catch (KafkaException exception)
        {
            DiscardIfFatal(exception.Error);
            return PublishResult.Failure(Categorize(exception.Error.Code), exception.Error.Code.ToString());
        }
    }

    public Task<TopicStatus> CheckTopicAsync(string topic, CancellationToken cancellationToken) =>
        Task.Run(() => CheckTopic(topic), cancellationToken);

    public void Dispose()
    {
        IProducer<string, byte[]>? producer;
        lock (_gate)
        {
            producer = _producer;
            _producer = null;
        }

        if (producer is not null)
        {
            // Every call awaited its delivery report, so nothing should be queued; the bound only guards a stuck broker.
            producer.Flush(TimeSpan.FromSeconds(5));
            producer.Dispose();
        }
    }

    /// <summary>The Kafka record of a row: the same bytes on every attempt, so a redelivery is byte-identical.</summary>
    internal static Message<string, byte[]> ToKafkaMessage(PendingOutboxMessage message) => new()
    {
        Key = message.MessageKey,
        Value = Encoding.UTF8.GetBytes(message.Envelope),
        Headers = new Headers
        {
            { "event-id", Encoding.UTF8.GetBytes(message.EventId.ToString("D")) },
            { "event-type", Encoding.UTF8.GetBytes(message.EventType) },
            { "event-version", Encoding.UTF8.GetBytes(message.EventVersion.ToString(CultureInfo.InvariantCulture)) },
            { "correlation-id", Encoding.UTF8.GetBytes(CorrelationIdOf(message.Envelope)) },
            { "content-type", "application/json"u8.ToArray() },
        },
    };

    /// <summary>The envelope's correlation ID. The body is authoritative; the header only repeats it.</summary>
    internal static string CorrelationIdOf(string envelope)
    {
        using var document = JsonDocument.Parse(envelope);
        return document.RootElement.GetProperty("correlationId").GetString() ?? string.Empty;
    }

    /// <summary>Maps a broker or client error to the bounded failure category.</summary>
    internal static PublishFailureCategory Categorize(ErrorCode code) => code switch
    {
        ErrorCode.UnknownTopicOrPart or ErrorCode.Local_UnknownTopic or ErrorCode.Local_UnknownPartition => PublishFailureCategory.Topic,

        ErrorCode.TopicAuthorizationFailed or ErrorCode.ClusterAuthorizationFailed or ErrorCode.GroupAuthorizationFailed
            or ErrorCode.TransactionalIdAuthorizationFailed or ErrorCode.SaslAuthenticationFailed or ErrorCode.Local_Authentication
            or ErrorCode.Local_Ssl => PublishFailureCategory.Authorization,

        ErrorCode.MsgSizeTooLarge or ErrorCode.InvalidMsg or ErrorCode.InvalidMsgSize
            or ErrorCode.Local_BadMsg or ErrorCode.RecordListTooLarge or ErrorCode.InvalidRecord
            or ErrorCode.PolicyViolation => PublishFailureCategory.Rejected,

        ErrorCode.Local_InvalidArg or ErrorCode.Local_Fatal or ErrorCode.InvalidRequiredAcks or ErrorCode.Local_Conflict
            or ErrorCode.Local_NotImplemented or ErrorCode.UnsupportedVersion or ErrorCode.Local_UnsupportedFeature
            or ErrorCode.UnsupportedForMessageFormat => PublishFailureCategory.Configuration,

        ErrorCode.Local_MsgTimedOut or ErrorCode.Local_TimedOut or ErrorCode.Local_Transport or ErrorCode.Local_AllBrokersDown
            or ErrorCode.Local_Resolve or ErrorCode.Local_QueueFull or ErrorCode.Local_Retry or ErrorCode.Local_Destroy
            or ErrorCode.RequestTimedOut or ErrorCode.NetworkException or ErrorCode.LeaderNotAvailable
            or ErrorCode.NotLeaderForPartition or ErrorCode.NotEnoughReplicas or ErrorCode.NotEnoughReplicasAfterAppend
            or ErrorCode.BrokerNotAvailable or ErrorCode.ReplicaNotAvailable or ErrorCode.NotCoordinatorForGroup
            or ErrorCode.KafkaStorageError or ErrorCode.OutOfOrderSequenceNumber => PublishFailureCategory.Transient,

        _ => PublishFailureCategory.Unknown,
    };

    private TopicStatus CheckTopic(string topic)
    {
        try
        {
            using var admin = new AdminClientBuilder(ClientConfig(options.Value)).Build();
            var metadata = admin.GetMetadata(topic, options.Value.MessageTimeout);
            var described = metadata.Topics.SingleOrDefault(candidate => candidate.Topic == topic);
            return described?.Error.Code switch
            {
                null => TopicStatus.Missing,
                ErrorCode.NoError when described.Partitions.Count > 0 => TopicStatus.Exists,
                ErrorCode.NoError => TopicStatus.Missing,
                ErrorCode.TopicAuthorizationFailed => TopicStatus.Unauthorized,
                _ => TopicStatus.Missing,
            };
        }
        catch (KafkaException exception)
        {
            LogMetadataFailed(logger, exception.Error.Code.ToString());
            return exception.Error.Code is ErrorCode.TopicAuthorizationFailed or ErrorCode.ClusterAuthorizationFailed
                ? TopicStatus.Unauthorized
                : TopicStatus.Unreachable;
        }
    }

    private IProducer<string, byte[]> Producer()
    {
        lock (_gate)
        {
            return _producer ??= new ProducerBuilder<string, byte[]>(ProducerConfig(options.Value))
                .SetErrorHandler((_, error) =>
                {
                    // librdkafka reports broker connection problems here and retries them itself; the relay acts on the
                    // delivery reports. A fatal error makes the idempotent producer unusable: the next call rebuilds it.
                    if (error.IsFatal)
                    {
                        LogFatalProducerError(logger, error.Code.ToString());
                    }
                    else
                    {
                        LogProducerError(logger, error.Code.ToString());
                    }
                })
                .Build();
        }
    }

    private void DiscardIfFatal(Error error)
    {
        if (!error.IsFatal)
        {
            return;
        }

        IProducer<string, byte[]>? producer;
        lock (_gate)
        {
            producer = _producer;
            _producer = null;
        }

        producer?.Dispose();
    }

    internal static ProducerConfig ProducerConfig(KafkaOptions settings) => new(ClientConfig(settings))
    {
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageTimeoutMs = (int)settings.MessageTimeout.TotalMilliseconds,

        // The relay sends one message and waits for it, so batching delay would only add latency.
        LingerMs = 0,

        // A producer never asks the broker to create a topic (ADR-023 section 2).
        AllowAutoCreateTopics = false,
    };

    internal static ClientConfig ClientConfig(KafkaOptions settings)
    {
        var config = new ClientConfig
        {
            BootstrapServers = settings.BootstrapServers,
            ClientId = ClientId,
            SecurityProtocol = settings.SecurityProtocol == KafkaSecurityProtocol.Ssl ? SecurityProtocol.Ssl : SecurityProtocol.Plaintext,
        };
        if (settings.SecurityProtocol == KafkaSecurityProtocol.Ssl)
        {
            config.SslCaLocation = settings.SslCaLocation;
            config.SslCertificateLocation = settings.SslCertificateLocation;
            config.SslKeyLocation = settings.SslKeyLocation;
        }

        return config;
    }

    [LoggerMessage(EventId = 3330, Level = LogLevel.Debug, Message = "Kafka producer reported {ErrorCode}; the client retries the connection")]
    private static partial void LogProducerError(ILogger logger, string errorCode);

    [LoggerMessage(EventId = 3331, Level = LogLevel.Error, Message = "Kafka producer reached a fatal state ({ErrorCode}); it is rebuilt for the next attempt")]
    private static partial void LogFatalProducerError(ILogger logger, string errorCode);

    [LoggerMessage(EventId = 3332, Level = LogLevel.Warning, Message = "Kafka topic metadata could not be read ({ErrorCode})")]
    private static partial void LogMetadataFailed(ILogger logger, string errorCode);
}
