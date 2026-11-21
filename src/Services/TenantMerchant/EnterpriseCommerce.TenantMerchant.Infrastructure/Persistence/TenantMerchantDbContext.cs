using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;

/// <summary>
/// The Tenant &amp; Merchant database (database per service, ADR-003 and ADR-011). It holds no foreign key or reference
/// to any other service's database; Identity subjects are stored as opaque external identifiers.
/// </summary>
/// <remarks>
/// Invariants that must hold under concurrency are database constraints: the unique slug, the unique provisioning key
/// per client, one current membership per subject and tenant, and the allowed status and role values. Every table has
/// a <c>rowversion</c> concurrency token for the updates that later slices add.
/// </remarks>
internal sealed class TenantMerchantDbContext(DbContextOptions<TenantMerchantDbContext> options) : DbContext(options)
{
    public const string TenantSlugIndex = "UX_Tenants_Slug";
    public const string ProvisioningKeyIndex = "UX_Tenants_ProvisionedByClientId_ProvisioningKey";
    public const string CurrentMembershipIndex = "UX_Memberships_TenantId_SubjectId_Current";
    public const string RowVersion = "RowVersion";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("Tenants", table => table.HasCheckConstraint("CK_Tenants_Status", AllowedValues<TenantStatus>("Status")));
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Id).ValueGeneratedNever();
            tenant.Property(t => t.Slug).HasMaxLength(TenantSlug.MaxLength).IsUnicode(false);
            tenant.Property(t => t.Status).HasConversion<string>().HasMaxLength(32);
            tenant.Property(t => t.ProvisionedByClientId).HasMaxLength(100);
            tenant.Property(t => t.ProvisioningKey).HasMaxLength(100);
            tenant.Property(t => t.ProvisioningRequestHash).HasMaxLength(32).IsFixedLength();
            tenant.Property(t => t.ProvisionedByActor).HasMaxLength(ActorReference.MaxLength);
            tenant.Property<byte[]>(RowVersion).IsRowVersion();

            tenant.HasIndex(t => t.Slug).IsUnique().HasDatabaseName(TenantSlugIndex);
            tenant.HasIndex(t => new { t.ProvisionedByClientId, t.ProvisioningKey }).IsUnique().HasDatabaseName(ProvisioningKeyIndex);

            // ADR-020: one merchant profile per tenant, in its own table, written in the tenant's transaction.
            tenant.OwnsOne(t => t.MerchantProfile, profile =>
            {
                profile.ToTable("MerchantProfiles");
                profile.WithOwner().HasForeignKey("TenantId");
                profile.HasKey("TenantId");
                profile.Property(p => p.DisplayName).HasMaxLength(MerchantDisplayName.MaxLength);
                profile.Property<byte[]>(RowVersion).IsRowVersion();
            });
            tenant.Navigation(t => t.MerchantProfile).IsRequired();
        });

        modelBuilder.Entity<Membership>(membership =>
        {
            membership.ToTable("Memberships", table =>
            {
                table.HasCheckConstraint("CK_Memberships_Role", AllowedValues<MerchantRole>("Role"));
                table.HasCheckConstraint("CK_Memberships_Status", AllowedValues<MembershipStatus>("Status"));
            });
            membership.HasKey(m => m.Id);
            membership.Property(m => m.Id).ValueGeneratedNever();
            membership.Property(m => m.SubjectId).HasMaxLength(SubjectId.MaxLength);
            membership.Property(m => m.Role).HasConversion<string>().HasMaxLength(32);
            membership.Property(m => m.Status).HasConversion<string>().HasMaxLength(32);
            membership.Property<byte[]>(RowVersion).IsRowVersion();

            membership.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Restrict);

            // One current membership per subject and tenant. A removed membership stays as history, so the subject can
            // join again later with a new membership.
            membership.HasIndex(m => new { m.TenantId, m.SubjectId })
                .IsUnique()
                .HasFilter($"[Status] <> N'{nameof(MembershipStatus.Removed)}'")
                .HasDatabaseName(CurrentMembershipIndex);

            // The membership listing (ordered by creation), the initial-owner lookup and the foreign key.
            membership.HasIndex(m => new { m.TenantId, m.CreatedAt });
        });
    }

    private static string AllowedValues<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"[{column}] IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(name => $"N'{name}'"))})";
}
