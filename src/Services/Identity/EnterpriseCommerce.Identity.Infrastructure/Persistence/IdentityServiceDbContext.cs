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
    }
}
