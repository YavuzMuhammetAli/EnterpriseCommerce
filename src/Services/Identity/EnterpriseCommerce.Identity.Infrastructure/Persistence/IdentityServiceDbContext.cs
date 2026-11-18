using EnterpriseCommerce.Identity.Domain.Security;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.Identity.Infrastructure.Persistence;

/// <summary>
/// Identity's own SQL Server database (ADR-003, ADR-011): ASP.NET Core Identity users (no role tables),
/// OpenIddict applications, authorizations, scopes and tokens, and the custom security tables.
/// No other service accesses this database.
/// </summary>
internal sealed class IdentityServiceDbContext(DbContextOptions<IdentityServiceDbContext> options)
    : IdentityUserContext<ApplicationUser>(options)
{
    public DbSet<SecurityVersionRecord> SecurityVersions => Set<SecurityVersionRecord>();

    public DbSet<RefreshTokenRedemption> RefreshTokenRedemptions => Set<RefreshTokenRedemption>();

    public DbSet<AccountSecurityToken> AccountSecurityTokens => Set<AccountSecurityToken>();

    public DbSet<SecurityEmailDeliveryRecord> SecurityEmailDeliveries => Set<SecurityEmailDeliveryRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.Status).HasConversion<string>().HasMaxLength(16);
        });

        builder.Entity<SecurityVersionRecord>(record =>
        {
            record.ToTable("SecurityVersions");
            record.HasKey(r => r.UserId);
            record.Property(r => r.UserId).HasMaxLength(450);
            record.HasOne<ApplicationUser>().WithOne().HasForeignKey<SecurityVersionRecord>(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<RefreshTokenRedemption>(redemption =>
        {
            redemption.ToTable("RefreshTokenRedemptions");
            redemption.HasKey(r => r.TokenId);
            redemption.Property(r => r.TokenId).HasMaxLength(100);
            redemption.Property(r => r.AuthorizationId).HasMaxLength(100);
            redemption.HasIndex(r => r.RedeemedAt);
        });

        builder.Entity<AccountSecurityToken>(token =>
        {
            token.ToTable("AccountSecurityTokens");
            token.HasKey(t => t.Id);
            token.Property(t => t.Id).ValueGeneratedNever();
            token.Property(t => t.UserId).HasMaxLength(450);
            token.Property(t => t.Purpose).HasConversion<string>().HasMaxLength(32);
            token.Property(t => t.TokenHash).HasMaxLength(AccountSecurityToken.TokenHashLength);
            token.HasIndex(t => t.TokenHash).IsUnique();
            token.HasIndex(t => new { t.UserId, t.Purpose });
            token.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<SecurityEmailDeliveryRecord>(delivery =>
        {
            delivery.ToTable("SecurityEmailDeliveries");
            delivery.HasKey(d => d.Id);
            delivery.Property(d => d.Id).ValueGeneratedNever();
            delivery.Property(d => d.UserId).HasMaxLength(450);
            delivery.Property(d => d.Purpose).HasConversion<string>().HasMaxLength(32);
            delivery.Property(d => d.Status).HasConversion<string>().HasMaxLength(16);
            delivery.Property(d => d.CreatedAt).HasColumnType("datetime2").HasDefaultValueSql("SYSUTCDATETIME()");
            delivery.Property(d => d.ClaimedAt).HasColumnType("datetime2");
            delivery.Property(d => d.CompletedAt).HasColumnType("datetime2");
            delivery.Property(d => d.ErrorCategory).HasMaxLength(64);

            // Rate-limit window per account and purpose, and the two reconciliation scans.
            delivery.HasIndex(d => new { d.UserId, d.Purpose, d.CreatedAt });
            delivery.HasIndex(d => new { d.Status, d.CreatedAt });
            delivery.HasIndex(d => new { d.Status, d.ClaimedAt });

            delivery.HasOne<ApplicationUser>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);
            delivery.HasOne<AccountSecurityToken>().WithMany().HasForeignKey(d => d.SecurityTokenId).OnDelete(DeleteBehavior.NoAction);
        });
    }
}
