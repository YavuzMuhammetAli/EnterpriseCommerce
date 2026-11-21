using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;

/// <summary>
/// The Tenant &amp; Merchant database (database per service, ADR-003 and ADR-011). It holds no foreign key or reference
/// to any other service's database; Identity subjects are stored as opaque external identifiers.
/// </summary>
/// <remarks>
/// <para>
/// Invariants that must hold under concurrency are database constraints: the unique slug, the unique provisioning key
/// per client, one current membership per subject and tenant, one pending invitation per address and tenant, a unique
/// invitation token hash, and the allowed status and role values. Every mutable table has a <c>rowversion</c>
/// concurrency token.
/// </para>
/// <para>
/// Two triggers back the application rules (ADR-021): <see cref="RequireActiveOwnerTrigger"/> refuses any change that
/// leaves a tenant without an active owner, and <see cref="AuditAppendOnlyTrigger"/> refuses every update and delete
/// of an audit record. The application never relies on them; they stop a path that bypasses its rules.
/// </para>
/// </remarks>
internal sealed class TenantMerchantDbContext(DbContextOptions<TenantMerchantDbContext> options) : DbContext(options)
{
    public const string TenantSlugIndex = "UX_Tenants_Slug";
    public const string ProvisioningKeyIndex = "UX_Tenants_ProvisionedByClientId_ProvisioningKey";
    public const string CurrentMembershipIndex = "UX_Memberships_TenantId_SubjectId_Current";
    public const string PendingInvitationIndex = "UX_MerchantInvitations_TenantId_NormalizedEmail_Pending";
    public const string InvitationTokenHashIndex = "UX_MerchantInvitations_TokenHash";
    public const string RowVersion = "RowVersion";

    /// <summary>Refuses a membership change that leaves a tenant without an active owner (error 50001).</summary>
    public const string RequireActiveOwnerTrigger = "TR_Memberships_RequireActiveOwner";

    /// <summary>Refuses updates and deletes of audit records (error 50002).</summary>
    public const string AuditAppendOnlyTrigger = "TR_MembershipAuditRecords_AppendOnly";

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<MerchantInvitation> MerchantInvitations => Set<MerchantInvitation>();

    public DbSet<MembershipAuditRecord> MembershipAuditRecords => Set<MembershipAuditRecord>();

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

                // Declared so that EF Core does not use OUTPUT without INTO, which SQL Server refuses on tables with triggers.
                table.HasTrigger(RequireActiveOwnerTrigger);
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

            // The active-owner count of the last-owner rule, and of its backstop trigger.
            membership.HasIndex(m => m.TenantId)
                .HasFilter($"[Role] = N'{nameof(MerchantRole.MerchantOwner)}' AND [Status] = N'{nameof(MembershipStatus.Active)}'")
                .HasDatabaseName("IX_Memberships_TenantId_ActiveOwner");

            // The caller's own tenants.
            membership.HasIndex(m => new { m.SubjectId, m.Status });
        });

        modelBuilder.Entity<MerchantInvitation>(invitation =>
        {
            invitation.ToTable("MerchantInvitations", table =>
            {
                // An invitation never grants MerchantOwner, which only provisioning creates (ADR-021).
                table.HasCheckConstraint(
                    "CK_MerchantInvitations_Role",
                    $"[Role] IN (N'{nameof(MerchantRole.MerchantAdmin)}', N'{nameof(MerchantRole.MerchantStaff)}')");
                table.HasCheckConstraint("CK_MerchantInvitations_Status", AllowedValues<InvitationStatus>("Status"));
                table.HasCheckConstraint("CK_MerchantInvitations_Expiry", "[ExpiresAt] > [CreatedAt]");

                // The state columns agree with the status, so that no row can claim an acceptance it does not record.
                table.HasCheckConstraint(
                    "CK_MerchantInvitations_Accepted",
                    "([Status] = N'Accepted' AND [AcceptedAt] IS NOT NULL AND [AcceptedBySubjectId] IS NOT NULL AND [AcceptedMembershipId] IS NOT NULL)"
                    + " OR ([Status] <> N'Accepted' AND [AcceptedAt] IS NULL AND [AcceptedBySubjectId] IS NULL AND [AcceptedMembershipId] IS NULL)");
                table.HasCheckConstraint(
                    "CK_MerchantInvitations_Cancelled",
                    "([Status] = N'Cancelled' AND [CancelledAt] IS NOT NULL AND [CancelledBySubjectId] IS NOT NULL)"
                    + " OR ([Status] <> N'Cancelled' AND [CancelledAt] IS NULL AND [CancelledBySubjectId] IS NULL)");
                table.HasCheckConstraint(
                    "CK_MerchantInvitations_Expired",
                    "([Status] = N'Expired' AND [ExpiredAt] IS NOT NULL) OR ([Status] <> N'Expired' AND [ExpiredAt] IS NULL)");
            });
            invitation.HasKey(i => i.Id);
            invitation.Property(i => i.Id).ValueGeneratedNever();
            invitation.Property(i => i.Email).HasMaxLength(InviteeEmail.MaxLength);
            invitation.Property(i => i.NormalizedEmail).HasMaxLength(InviteeEmail.MaxLength);
            invitation.Property(i => i.Role).HasConversion<string>().HasMaxLength(32);
            invitation.Property(i => i.Status).HasConversion<string>().HasMaxLength(32);
            invitation.Property(i => i.InvitedBySubjectId).HasMaxLength(SubjectId.MaxLength);
            invitation.Property(i => i.AcceptedBySubjectId).HasMaxLength(SubjectId.MaxLength);
            invitation.Property(i => i.CancelledBySubjectId).HasMaxLength(SubjectId.MaxLength);
            invitation.Property(i => i.TokenHash)
                .HasField("_tokenHash")
                .UsePropertyAccessMode(PropertyAccessMode.Field)
                .HasMaxLength(InvitationTokenHash.Length)
                .IsFixedLength();
            invitation.Property<byte[]>(RowVersion).IsRowVersion();

            invitation.HasOne<Tenant>().WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.Restrict);

            invitation.HasIndex(i => i.TokenHash).IsUnique().HasDatabaseName(InvitationTokenHashIndex);

            // At most one pending invitation per address and tenant. Closed invitations stay as history.
            invitation.HasIndex(i => new { i.TenantId, i.NormalizedEmail })
                .IsUnique()
                .HasFilter($"[Status] = N'{nameof(InvitationStatus.Pending)}'")
                .HasDatabaseName(PendingInvitationIndex);

            // The invitation listing (newest first) and the foreign key.
            invitation.HasIndex(i => new { i.TenantId, i.CreatedAt });
        });

        modelBuilder.Entity<MembershipAuditRecord>(audit =>
        {
            audit.ToTable("MembershipAuditRecords", table =>
            {
                table.HasCheckConstraint("CK_MembershipAuditRecords_Action", AllowedValues<MembershipAuditAction>("Action"));
                table.HasCheckConstraint("CK_MembershipAuditRecords_ActorKind", AllowedValues<AuditActorKind>("ActorKind"));
                table.HasTrigger(AuditAppendOnlyTrigger);
            });
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Id).ValueGeneratedNever();
            audit.Property(a => a.Action).HasConversion<string>().HasMaxLength(64);
            audit.Property(a => a.ActorKind).HasConversion<string>().HasMaxLength(16);
            audit.Property(a => a.ActorId).HasMaxLength(SubjectId.MaxLength);
            audit.Property(a => a.SubjectId).HasMaxLength(SubjectId.MaxLength);
            audit.Property(a => a.PreviousRole).HasMaxLength(32);
            audit.Property(a => a.NewRole).HasMaxLength(32);
            audit.Property(a => a.PreviousStatus).HasMaxLength(32);
            audit.Property(a => a.NewStatus).HasMaxLength(32);
            audit.Property(a => a.CorrelationId).HasMaxLength(MembershipAuditRecord.CorrelationIdMaxLength);

            // Append-only: no rowversion, because a record is never updated (the trigger refuses it).
            audit.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Restrict);
            audit.HasIndex(a => new { a.TenantId, a.OccurredAt });
        });
    }

    private static string AllowedValues<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"[{column}] IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(name => $"N'{name}'"))})";
}
