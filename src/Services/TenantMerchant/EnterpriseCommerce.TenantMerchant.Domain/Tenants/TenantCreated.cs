using EnterpriseCommerce.TenantMerchant.Domain.Common;

namespace EnterpriseCommerce.TenantMerchant.Domain.Tenants;

/// <summary>
/// A tenant was created, with its merchant profile (domain event). Recorded once by <see cref="Tenant.Provision"/> and
/// <see cref="Tenant.CreateBySelfService"/>. It is internal; the published contract is <c>TenantCreated</c> v1
/// (docs/events/TENANT_MERCHANT_EVENTS.md), which states the committed creation of the tenant, its profile, its initial
/// owner membership and the membership's audit record. It carries no subject, client, key or actor values.
/// </summary>
/// <param name="TenantId">The new tenant.</param>
/// <param name="Slug">Its immutable slug.</param>
/// <param name="DisplayName">The merchant display name, as stored.</param>
/// <param name="Status">The status at creation.</param>
/// <param name="Origin">How the tenant was created.</param>
/// <param name="OccurredAt">The tenant's creation time.</param>
public sealed record TenantCreated(
    Guid TenantId,
    string Slug,
    string DisplayName,
    TenantStatus Status,
    TenantOrigin Origin,
    DateTimeOffset OccurredAt) : IDomainEvent;
