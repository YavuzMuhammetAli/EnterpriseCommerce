namespace EnterpriseCommerce.TenantMerchant.Domain.Tenants;

/// <summary>The lifecycle status of a tenant. Suspension and closure arrive with Admin tenant management.</summary>
public enum TenantStatus
{
    Active,
}

/// <summary>How a tenant came into existence.</summary>
public enum TenantOrigin
{
    /// <summary>Provisioned by an allow-listed service caller (Admin, in future) for an owner subject.</summary>
    Provisioned,

    /// <summary>Created by a merchant-portal user, who becomes its owner (PHASE_3_S3_SELF_SERVICE_ONBOARDING.md).</summary>
    SelfService,
}

/// <summary>
/// A tenant: one merchant organization and the logical isolation boundary of ADR-014 (ADR-020: tenant and merchant are
/// one-to-one). Every tenant-owned record of every service carries its <see cref="Id"/>.
/// </summary>
public sealed class Tenant
{
    private Tenant()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>The immutable, platform-unique handle (<see cref="TenantSlug"/>).</summary>
    public string Slug { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public TenantOrigin Origin { get; private set; }

    /// <summary>
    /// The Identity subject that created a <see cref="TenantOrigin.SelfService"/> tenant; <c>null</c> for a provisioned
    /// one. It scopes the self-service idempotency key and counts against the per-subject limit.
    /// </summary>
    public string? SelfServiceSubjectId { get; private set; }

    /// <summary>The merchant profile of the tenant's single merchant organization.</summary>
    public MerchantProfile MerchantProfile { get; private set; } = null!;

    /// <summary>
    /// The <c>svc-</c> service client that provisioned the tenant, or for a self-service tenant the merchant-portal
    /// client the user came through (audit only).
    /// </summary>
    public string ProvisionedByClientId { get; private set; } = string.Empty;

    /// <summary>
    /// The caller's <c>Idempotency-Key</c> for the creating request: unique per client for a provisioned tenant, and
    /// per subject for a self-service tenant.
    /// </summary>
    public string ProvisioningKey { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the canonical provisioning request, to tell a retry from a reused key.</summary>
    public byte[] ProvisioningRequestHash { get; private set; } = [];

    /// <summary>
    /// The person who asked for the tenant, for audit only: the service caller's opaque actor reference, or for a
    /// self-service tenant the creating subject.
    /// </summary>
    public string ProvisionedByActor { get; private set; } = string.Empty;

    /// <summary>
    /// Creates an active tenant with its merchant profile. The initial <see cref="Memberships.MerchantRole.MerchantOwner"/>
    /// membership is created alongside it, in the same transaction, by the provisioning use case.
    /// </summary>
    public static Tenant Provision(
        TenantSlug slug,
        MerchantDisplayName displayName,
        ProvisioningRecord provisioning,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(provisioning);

        return new Tenant
        {
            Id = Guid.CreateVersion7(now),
            Slug = slug.Value,
            Status = TenantStatus.Active,
            CreatedAt = now,
            Origin = TenantOrigin.Provisioned,
            MerchantProfile = new MerchantProfile(displayName.Value, now),
            ProvisionedByClientId = provisioning.ClientId,
            ProvisioningKey = provisioning.Key,
            ProvisioningRequestHash = [.. provisioning.RequestHash],
            ProvisionedByActor = provisioning.Actor.Value,
        };
    }

    /// <summary>
    /// Creates an active tenant with its merchant profile for the merchant-portal user who asked for it (PHASE_2_2
    /// section 5.1.2). The user's <see cref="Memberships.MerchantRole.MerchantOwner"/> membership is created alongside it,
    /// in the same transaction, by the onboarding use case.
    /// </summary>
    public static Tenant CreateBySelfService(
        TenantSlug slug,
        MerchantDisplayName displayName,
        SelfServiceRecord request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(slug);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(request);

        return new Tenant
        {
            Id = Guid.CreateVersion7(now),
            Slug = slug.Value,
            Status = TenantStatus.Active,
            CreatedAt = now,
            Origin = TenantOrigin.SelfService,
            SelfServiceSubjectId = request.Subject.Value,
            MerchantProfile = new MerchantProfile(displayName.Value, now),
            ProvisionedByClientId = request.ClientId,
            ProvisioningKey = request.Key,
            ProvisioningRequestHash = [.. request.RequestHash],
            ProvisionedByActor = request.Subject.Value,
        };
    }
}

/// <summary>The merchant profile of a tenant: what the marketplace shows about the merchant.</summary>
public sealed class MerchantProfile
{
    private MerchantProfile()
    {
    }

    internal MerchantProfile(string displayName, DateTimeOffset now)
    {
        DisplayName = displayName;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string DisplayName { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>Who provisioned a tenant, and with which request.</summary>
/// <param name="ClientId">The authenticated <c>svc-</c> client ID of the caller.</param>
/// <param name="Key">The caller's idempotency key.</param>
/// <param name="RequestHash">SHA-256 (32 bytes) of the canonical request.</param>
/// <param name="Actor">The caller's opaque actor reference.</param>
public sealed record ProvisioningRecord(string ClientId, string Key, IReadOnlyList<byte> RequestHash, ActorReference Actor);

/// <summary>Who created a self-service tenant, and with which request.</summary>
/// <param name="ClientId">The <c>client_id</c> of the user's access token (the merchant portal's BFF), for audit.</param>
/// <param name="Key">The user's idempotency key, unique per subject.</param>
/// <param name="RequestHash">SHA-256 (32 bytes) of the canonical request.</param>
/// <param name="Subject">The authenticated user, who becomes the owner.</param>
public sealed record SelfServiceRecord(string ClientId, string Key, IReadOnlyList<byte> RequestHash, Memberships.SubjectId Subject);
