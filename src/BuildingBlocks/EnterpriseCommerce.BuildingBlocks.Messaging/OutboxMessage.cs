namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>
/// One serialized integration event message waiting for, or done with, publication (ADR-023 section 5). Written in the
/// business transaction by the publishing service's <c>DbContext</c>; read and marked only by the relay. It is
/// technical: no business code reads it, and it has no foreign key to a business table.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>The topic-name limit of Kafka.</summary>
    public const int TopicMaxLength = 249;

    public const int MessageKeyMaxLength = 64;
    public const int EventTypeMaxLength = 128;
    public const int LastErrorMaxLength = 512;

    /// <summary>The envelope limit of ADR-023 section 3, in UTF-8 bytes.</summary>
    public const int EnvelopeMaxBytes = 65536;

    /// <summary>The publication order: an identity value, the clustered primary key.</summary>
    public long Sequence { get; private set; }

    /// <summary>The envelope's <c>eventId</c>: the identity of the logical fact, shared by every version of it.</summary>
    public Guid EventId { get; init; }

    /// <summary>The topic, fixed when the row is written.</summary>
    public string Topic { get; init; } = string.Empty;

    /// <summary>The Kafka message key: the tenant ID (lowercase GUID, <c>D</c> format).</summary>
    public string MessageKey { get; init; } = string.Empty;

    public string EventType { get; init; } = string.Empty;

    /// <summary>The payload contract version of this message. (<see cref="EventId"/>, <see cref="EventVersion"/>) is unique.</summary>
    public int EventVersion { get; init; }

    /// <summary>The exact JSON that is published, at most <see cref="EnvelopeMaxBytes"/> as UTF-8.</summary>
    public string Envelope { get; init; } = string.Empty;

    /// <summary>The business time of the fact.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary><c>null</c> while the message is pending; set after the broker acknowledged it.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    /// <summary>Failed publication attempts so far.</summary>
    public int Attempts { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    /// <summary>The last failure's category and error code, never the payload.</summary>
    public string? LastError { get; private set; }
}
