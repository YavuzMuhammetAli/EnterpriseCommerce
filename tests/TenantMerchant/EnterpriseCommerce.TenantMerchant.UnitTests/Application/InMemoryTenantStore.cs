using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Application;

/// <summary>
/// An in-memory stand-in for the two ports, for use-case logic only. Atomicity, constraints and concurrency are proven
/// against SQL Server in the integration tests, never here.
/// </summary>
internal sealed class InMemoryTenantStore : ITenantProvisioningStore, ITenantReadStore
{
    public List<Tenant> Tenants { get; } = [];

    public List<Membership> Memberships { get; } = [];

    /// <summary>Runs inside <see cref="AddAsync"/> before the uniqueness checks, to simulate a concurrent winner.</summary>
    public Action? BeforeInsert { get; set; }

    public int Inserts { get; private set; }

    public Task<ProvisionedTenant?> FindByProvisioningKeyAsync(string clientId, string provisioningKey, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants
            .Where(t => t.ProvisionedByClientId == clientId && t.ProvisioningKey == provisioningKey)
            .Select(t => new ProvisionedTenant(View(t), t.ProvisioningRequestHash))
            .SingleOrDefault());

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken)
    {
        BeforeSlugCheck?.Invoke();
        return Task.FromResult(Tenants.Any(t => t.Slug == slug));
    }

    /// <summary>Runs inside <see cref="SlugExistsAsync"/>, to simulate a commit between the key and slug lookups.</summary>
    public Action? BeforeSlugCheck { get; set; }

    public Task<TenantInsertOutcome> AddAsync(Tenant tenant, Membership owner, CancellationToken cancellationToken)
    {
        BeforeInsert?.Invoke();
        if (Tenants.Any(t => t.Slug == tenant.Slug
            || (t.ProvisionedByClientId == tenant.ProvisionedByClientId && t.ProvisioningKey == tenant.ProvisioningKey)))
        {
            return Task.FromResult(TenantInsertOutcome.UniqueConflict);
        }

        Tenants.Add(tenant);
        Memberships.Add(owner);
        Inserts++;
        return Task.FromResult(TenantInsertOutcome.Inserted);
    }

    public Task<TenantView?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Tenants.Where(t => t.Id == tenantId).Select(View).SingleOrDefault());

    public Task<MembershipView?> GetCurrentMembershipAsync(Guid tenantId, string subjectId, CancellationToken cancellationToken) =>
        Task.FromResult(Memberships
            .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId && m.Status != MembershipStatus.Removed)
            .Select(View)
            .SingleOrDefault());

    public Task<MembershipView?> GetInitialOwnerAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(Memberships
            .Where(m => m.TenantId == tenantId && m.Role == MerchantRole.MerchantOwner)
            .OrderBy(m => m.CreatedAt)
            .Select(View)
            .FirstOrDefault());

    public Task<MembershipPage> ListMembershipsAsync(Guid tenantId, int skip, int take, CancellationToken cancellationToken)
    {
        var current = Memberships.Where(m => m.TenantId == tenantId && m.Status != MembershipStatus.Removed).OrderBy(m => m.CreatedAt).ToList();
        return Task.FromResult(new MembershipPage([.. current.Skip(skip).Take(take).Select(View)], 0, take, current.Count));
    }

    private static TenantView View(Tenant tenant) =>
        new(tenant.Id, tenant.Slug, tenant.Status, tenant.CreatedAt, tenant.MerchantProfile.DisplayName);

    private static MembershipView View(Membership membership) =>
        new(membership.Id, membership.TenantId, membership.SubjectId, membership.Role, membership.Status, membership.CreatedAt);
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
