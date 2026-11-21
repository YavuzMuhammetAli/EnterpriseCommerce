using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="ITenantProvisioningStore"/>.</summary>
internal sealed class TenantProvisioningStore(TenantMerchantDbContext context) : ITenantProvisioningStore
{
    public Task<ProvisionedTenant?> FindByProvisioningKeyAsync(string clientId, string provisioningKey, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("find tenant by provisioning key", async () =>
        {
            var row = await context.Tenants
                .AsNoTracking()
                .Where(t => t.ProvisionedByClientId == clientId && t.ProvisioningKey == provisioningKey)
                .Select(t => new { Tenant = new TenantView(t.Id, t.Slug, t.Status, t.CreatedAt, t.MerchantProfile.DisplayName), t.ProvisioningRequestHash })
                .SingleOrDefaultAsync(cancellationToken);
            return row is null ? null : new ProvisionedTenant(row.Tenant, row.ProvisioningRequestHash);
        });

    public Task<bool> SlugExistsAsync(string slug, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("check tenant slug", () => context.Tenants.AnyAsync(t => t.Slug == slug, cancellationToken));

    public async Task<TenantInsertOutcome> AddAsync(Tenant tenant, Membership owner, MembershipAuditRecord audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(audit);

        context.Tenants.Add(tenant);
        context.Memberships.Add(owner);
        context.MembershipAuditRecords.Add(audit);
        try
        {
            // One SaveChanges is one transaction: the tenant, its merchant profile, the owner membership and its audit
            // record commit together or not at all.
            return await SqlServerErrors.GuardAsync("insert tenant", async () =>
            {
                try
                {
                    await context.SaveChangesAsync(cancellationToken);
                    return TenantInsertOutcome.Inserted;
                }
                catch (DbUpdateException exception) when (SqlServerErrors.IsUniqueViolation(exception))
                {
                    return TenantInsertOutcome.UniqueConflict;
                }
            });
        }
        finally
        {
            // Nothing stays tracked: after a failure the entities were never written, after success they are done.
            context.ChangeTracker.Clear();
        }
    }
}

/// <summary>EF Core implementation of <see cref="ITenantReadStore"/>. Every membership query is scoped to one tenant.</summary>
internal sealed class TenantReadStore(TenantMerchantDbContext context) : ITenantReadStore
{
    public Task<TenantView?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("read tenant", () => context.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantView(t.Id, t.Slug, t.Status, t.CreatedAt, t.MerchantProfile.DisplayName))
            .SingleOrDefaultAsync(cancellationToken));

    public Task<MembershipView?> GetCurrentMembershipAsync(Guid tenantId, string subjectId, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("read membership", () => Views(context.Memberships
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.SubjectId == subjectId && m.Status != MembershipStatus.Removed))
            .SingleOrDefaultAsync(cancellationToken));

    public Task<MembershipView?> GetInitialOwnerAsync(Guid tenantId, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("read initial owner", () => Views(context.Memberships
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.Role == MerchantRole.MerchantOwner)
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.Id))
            .FirstOrDefaultAsync(cancellationToken));

    public Task<MembershipPage> ListMembershipsAsync(Guid tenantId, int skip, int take, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("list memberships", async () =>
        {
            var current = context.Memberships
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.Status != MembershipStatus.Removed);

            var total = await current.CountAsync(cancellationToken);
            var items = await Views(current.OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).Skip(skip).Take(take))
                .ToListAsync(cancellationToken);
            return new MembershipPage(items, Page: 0, take, total);
        });

    public Task<MembershipView?> GetMembershipAsync(Guid tenantId, Guid membershipId, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("read membership by id", () => Views(context.Memberships
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.Id == membershipId))
            .SingleOrDefaultAsync(cancellationToken));

    public Task<CallerTenantPage> ListCallerTenantsAsync(string subjectId, int skip, int take, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("list caller tenants", async () =>
        {
            var active = context.Memberships
                .AsNoTracking()
                .Where(m => m.SubjectId == subjectId && m.Status == MembershipStatus.Active);

            var total = await active.CountAsync(cancellationToken);
            var items = await (
                    from m in active
                    join t in context.Tenants.AsNoTracking() on m.TenantId equals t.Id
                    orderby m.CreatedAt, m.Id
                    select new CallerTenantView(
                        new TenantView(t.Id, t.Slug, t.Status, t.CreatedAt, t.MerchantProfile.DisplayName),
                        new MembershipView(m.Id, m.TenantId, m.SubjectId, m.Role, m.Status, m.CreatedAt, m.StatusChangedAt)))
                .Skip(skip)
                .Take(take)
                .ToListAsync(cancellationToken);
            return new CallerTenantPage(items, Page: 0, take, total);
        });

    private static IQueryable<MembershipView> Views(IQueryable<Membership> memberships) =>
        memberships.Select(m => new MembershipView(m.Id, m.TenantId, m.SubjectId, m.Role, m.Status, m.CreatedAt, m.StatusChangedAt));
}
