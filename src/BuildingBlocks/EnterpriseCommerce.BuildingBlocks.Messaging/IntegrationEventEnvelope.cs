namespace EnterpriseCommerce.BuildingBlocks.Messaging;

/// <summary>
/// The self-contained body of every integration event message (ADR-023 section 4,
/// <c>docs/events/schemas/envelope.v1.schema.json</c>). The same bytes are stored in the outbox and published.
/// </summary>
/// <typeparam name="TPayload">The payload contract of (<see cref="Producer"/>, <see cref="EventType"/>, <see cref="EventVersion"/>).</typeparam>
/// <param name="EventId">The identity of the logical business fact (GUID version 7), shared by every version of the fact.</param>
/// <param name="EventType">Past-tense PascalCase name, unique within the producer.</param>
/// <param name="EventVersion">The payload contract version of this message.</param>
/// <param name="PublishedVersions">The versions in which the fact is published: identical in every message of the fact, ascending, containing <paramref name="EventVersion"/>.</param>
/// <param name="Producer">The producing service.</param>
/// <param name="AggregateId">The aggregate the fact is about.</param>
/// <param name="TenantId">The tenant the message belongs to; <c>null</c> only for a message that is not tenant-scoped.</param>
/// <param name="OccurredAtUtc">The business time of the fact.</param>
/// <param name="CorrelationId">The flow's correlation ID.</param>
/// <param name="CausationId">The causing message's <c>eventId</c>, or for a root message the request's correlation ID.</param>
/// <param name="Payload">The event-specific data.</param>
public sealed record IntegrationEventEnvelope<TPayload>(
    Guid EventId,
    string EventType,
    int EventVersion,
    IReadOnlyList<int> PublishedVersions,
    string Producer,
    Guid AggregateId,
    Guid? TenantId,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    string CausationId,
    TPayload Payload)
    where TPayload : notnull;
