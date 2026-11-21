using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;

namespace EnterpriseCommerce.TenantMerchant.Application.Provisioning;

/// <summary>A tenant found by its provisioning key, with the hash of the request that created it.</summary>
public sealed record ProvisionedTenant(TenantView Tenant, IReadOnlyList<byte> RequestHash);

/// <summary>The result of inserting a new tenant.</summary>
public enum TenantInsertOutcome
{
    Inserted,

    /// <summary>
    /// A unique constraint rejected the insert (the slug, or the client's provisioning key): a concurrent or earlier
    /// request holds it. Nothing was written.
    /// </summary>
    UniqueConflict,
}

/// <summary>Persistence for tenant provisioning. Not a generic repository: it offers exactly what the use case needs.</summary>
public interface ITenantProvisioningStore
{
    Task<ProvisionedTenant?> FindByProvisioningKeyAsync(string clientId, string provisioningKey, CancellationToken cancellationToken);

    Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts the tenant, its merchant profile and the owner membership in <b>one</b> transaction: either all of them
    /// commit, or none does.
    /// </summary>
    Task<TenantInsertOutcome> AddAsync(Tenant tenant, Membership owner, CancellationToken cancellationToken);
}
