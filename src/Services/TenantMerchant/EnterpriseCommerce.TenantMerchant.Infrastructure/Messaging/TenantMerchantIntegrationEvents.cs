using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Domain.Common;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

/// <summary>
/// The one explicit mapper from Tenant &amp; Merchant domain events to integration event contracts (ADR-023 sections 5
/// and 8). It is the only messaging code that knows business types. Contracts: <c>docs/events/TENANT_MERCHANT_EVENTS.md</c>.
/// </summary>
internal static class TenantMerchantIntegrationEvents
{
    /// <summary>The producer name in every envelope.</summary>
    public const string Producer = "tenant-merchant";

    public const string TenantCreatedEventType = "TenantCreated";

    /// <summary>
    /// The versions each public event is published in. One version normally; a breaking change lists the new version
    /// next to the old one for the dual-publication period (docs/events/README.md, "A breaking change").
    /// </summary>
    public static IReadOnlyList<int> TenantCreatedVersions { get; } = [1];

    /// <summary>The outbox rows of one domain event: one per published version, sharing one event ID.</summary>
    /// <exception cref="InvalidOperationException">The event has no mapping, or breaks a contract rule.</exception>
    public static IReadOnlyList<OutboxMessage> ToOutboxMessages(IDomainEvent domainEvent, MessageMetadata metadata, string topic) =>
        IntegrationEventSerializer.ToOutboxMessages(ToFact(domainEvent), Producer, metadata, topic);

    /// <summary>
    /// Maps a domain event to its fact. Every domain event of the service is public today; an internal one must be listed
    /// here explicitly, so that a new event is never silently left unpublished.
    /// </summary>
    public static IntegrationEventFact ToFact(IDomainEvent domainEvent) => domainEvent switch
    {
        TenantCreated created => new IntegrationEventFact(
            Guid.CreateVersion7(created.OccurredAt),
            TenantCreatedEventType,
            created.TenantId,
            created.TenantId,
            created.OccurredAt,
            [.. TenantCreatedVersions.Select(version => new IntegrationEventVersion(version, TenantCreatedPayload(created, version)))]),
        _ => throw new InvalidOperationException($"The domain event {domainEvent.GetType().Name} has no integration event mapping."),
    };

    private static object TenantCreatedPayload(TenantCreated created, int version) => version switch
    {
        1 => new TenantCreatedV1Payload(created.Slug, created.DisplayName, created.Status.ToString(), created.Origin.ToString()),
        _ => throw new InvalidOperationException($"TenantCreated v{version} has no payload mapping."),
    };
}
