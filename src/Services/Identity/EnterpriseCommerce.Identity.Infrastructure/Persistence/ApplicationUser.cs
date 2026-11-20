using EnterpriseCommerce.Identity.Domain.Accounts;
using Microsoft.AspNetCore.Identity;

namespace EnterpriseCommerce.Identity.Infrastructure.Persistence;

/// <summary>
/// The ASP.NET Core Identity user entity. Framework type, kept in Infrastructure (ADR-015 A6).
/// It carries no roles: ASP.NET Core Identity roles are not authoritative (ADR-015 A3).
/// </summary>
public sealed class ApplicationUser : IdentityUser
{
    /// <summary>Dedicated account status. Disablement is never represented by lockout (ADR-017 section 9).</summary>
    public AccountStatus Status { get; set; } = AccountStatus.Active;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Database time of the last administrative status change.</summary>
    public DateTimeOffset? StatusChangedAt { get; set; }

    /// <summary>The reason code of the last administrative status change (<see cref="AccountStatusReason"/>).</summary>
    public AccountStatusReason? StatusReason { get; set; }

    /// <summary>Admin's opaque reference to the administrator who made the last status change. Audit only.</summary>
    public string? StatusChangedBy { get; set; }
}
