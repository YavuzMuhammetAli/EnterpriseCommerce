using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using EnterpriseCommerce.TenantMerchant.Application.Common;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

/// <summary>
/// One logical business fact to publish (ADR-023 section 3): one <see cref="EventId"/>, and one payload per published
/// version. A fact normally has one version; during a dual publication it has two, written in the same transaction
/// with the same event ID and the same <c>publishedVersions</c>.
/// </summary>
/// <param name="EventId">The identity of the fact, created once.</param>
/// <param name="EventType">The event type.</param>
/// <param name="AggregateId">The aggregate the fact is about.</param>
/// <param name="TenantId">The tenant, or <c>null</c> for a fact that is not tenant-scoped.</param>
/// <param name="OccurredAt">The business time of the fact.</param>
/// <param name="Versions">The payload of each published version.</param>
internal sealed record IntegrationEventFact(
    Guid EventId,
    string EventType,
    Guid AggregateId,
    Guid? TenantId,
    DateTimeOffset OccurredAt,
    IReadOnlyList<IntegrationEventVersion> Versions);

/// <summary>One version of a fact: the payload contract version and its payload.</summary>
internal sealed record IntegrationEventVersion(int Version, object Payload);

/// <summary>
/// Serializes integration events into envelopes and outbox rows with the encoding of ADR-023 section 3: UTF-8 JSON,
/// camelCase, UTC timestamps with <c>Z</c>, lowercase <c>D</c> GUIDs, at most 64 KiB. Every rule a consumer would
/// treat as malformed is checked here, before the row is written, so the transaction fails instead of the relay.
/// </summary>
internal static partial class IntegrationEventSerializer
{
    /// <summary>The serializer settings of every envelope. Read-only.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Creates the outbox rows of one fact: one row per version, all with the fact's event ID, and all with the same
    /// <c>publishedVersions</c>, computed once here from the versions the fact is published in.
    /// </summary>
    public static IReadOnlyList<OutboxMessage> ToOutboxMessages(
        IntegrationEventFact fact,
        string producer,
        MessageMetadata metadata,
        string topic)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrEmpty(topic);

        if (fact.Versions.Count == 0)
        {
            throw new InvalidOperationException($"The {fact.EventType} fact has no published version.");
        }

        // Computed once per fact, ascending, and shared by every version's envelope (the producer obligation of
        // docs/events/README.md: identical in every message of the fact).
        int[] publishedVersions = [.. fact.Versions.Select(version => version.Version).Order()];
        if (publishedVersions.Distinct().Count() != publishedVersions.Length)
        {
            throw new InvalidOperationException($"The {fact.EventType} fact lists a version more than once.");
        }

        var messageKey = (fact.TenantId ?? fact.AggregateId).ToString("D");
        return
        [
            .. fact.Versions.Select(version =>
            {
                var envelope = new IntegrationEventEnvelope<object>(
                    fact.EventId,
                    fact.EventType,
                    version.Version,
                    publishedVersions,
                    producer,
                    fact.AggregateId,
                    fact.TenantId,
                    fact.OccurredAt,
                    metadata.CorrelationId,
                    metadata.CausationId,
                    version.Payload);

                return new OutboxMessage
                {
                    EventId = fact.EventId,
                    Topic = topic,
                    MessageKey = messageKey,
                    EventType = fact.EventType,
                    EventVersion = version.Version,
                    Envelope = Serialize(envelope),
                    OccurredAt = fact.OccurredAt,
                };
            }),
        ];
    }

    /// <summary>Serializes one envelope after checking the rules a consumer relies on.</summary>
    /// <exception cref="InvalidOperationException">The envelope breaks a contract rule; nothing may be written.</exception>
    public static string Serialize<TPayload>(IntegrationEventEnvelope<TPayload> envelope)
        where TPayload : notnull
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.EventId.Version != 7)
        {
            throw new InvalidOperationException("An event ID must be a version 7 GUID.");
        }

        if (!EventTypePattern().IsMatch(envelope.EventType))
        {
            throw new InvalidOperationException("The event type must be a PascalCase name.");
        }

        if (!ProducerPattern().IsMatch(envelope.Producer))
        {
            throw new InvalidOperationException("The producer must be a lowercase service name.");
        }

        if (envelope.EventVersion < 1
            || envelope.PublishedVersions.Count == 0
            || envelope.PublishedVersions.Any(version => version < 1)
            || !envelope.PublishedVersions.SequenceEqual(envelope.PublishedVersions.Order().Distinct()))
        {
            throw new InvalidOperationException("The event version and the published versions must be positive, ascending and unique.");
        }

        // A message whose eventVersion is not in its own publishedVersions is malformed (ADR-023 section 7).
        if (!envelope.PublishedVersions.Contains(envelope.EventVersion))
        {
            throw new InvalidOperationException("The event version must be one of the published versions.");
        }

        if (!TraceIdentifierPattern().IsMatch(envelope.CorrelationId) || !TraceIdentifierPattern().IsMatch(envelope.CausationId))
        {
            throw new InvalidOperationException("The correlation and causation IDs must be 1 to 64 of A-Z a-z 0-9 - _ . :");
        }

        var json = JsonSerializer.Serialize(envelope, Options);
        if (Encoding.UTF8.GetByteCount(json) > OutboxMessage.EnvelopeMaxBytes)
        {
            throw new InvalidOperationException($"The {envelope.EventType} envelope exceeds {OutboxMessage.EnvelopeMaxBytes} bytes.");
        }

        return json;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        options.Converters.Add(new UtcTimestampConverter());
        options.MakeReadOnly();
        return options;
    }

    [GeneratedRegex("^[A-Z][A-Za-z0-9]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypePattern();

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProducerPattern();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceIdentifierPattern();

    /// <summary>Writes timestamps in UTC with a trailing <c>Z</c>; reads only UTC timestamps.</summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.GetString();
            if (text is null
                || !text.EndsWith('Z')
                || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            {
                throw new JsonException("A timestamp must be ISO-8601 in UTC with a trailing Z.");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}
