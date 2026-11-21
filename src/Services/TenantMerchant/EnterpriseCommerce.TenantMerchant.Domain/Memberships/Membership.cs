namespace EnterpriseCommerce.TenantMerchant.Domain.Memberships;

/// <summary>
/// The status of a membership. <c>Active</c> and <c>Suspended</c> alternate; <c>Removed</c> is terminal, and a later
/// membership of the same subject is a new membership.
/// </summary>
public enum MembershipStatus
{
    Active,
    Suspended,
    Removed,
}

/// <summary>The outcome of a membership change. Invalid changes are outcomes, not exceptions.</summary>
public enum MembershipChangeOutcome
{
    Changed,
    Unchanged,

    /// <summary>The status transition is not allowed (for example anything out of <c>Removed</c>).</summary>
    InvalidTransition,

    /// <summary><see cref="MerchantRole.MerchantOwner"/> cannot be assigned through a role change.</summary>
    OwnerRoleReserved,

    /// <summary>The change would leave the tenant without an active owner.</summary>
    LastActiveOwner,
}

/// <summary>
/// A subject's membership in one tenant, with one merchant role. Its own aggregate: a tenant's memberships are
/// unbounded, and each one changes independently.
/// </summary>
public sealed class Membership
{
    private Membership()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The Identity subject (<c>sub</c>) of the member.</summary>
    public string SubjectId { get; private set; } = string.Empty;

    public MerchantRole Role { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>
    /// The only way to obtain <see cref="MerchantRole.MerchantOwner"/>: the initial owner, created by trusted tenant
    /// provisioning in the same transaction as the tenant.
    /// </summary>
    public static Membership CreateOwner(Guid tenantId, SubjectId subject, DateTimeOffset now) =>
        Create(tenantId, subject, MerchantRole.MerchantOwner, now);

    /// <summary>Creates a non-owner membership. Owner rights are never granted this way.</summary>
    /// <exception cref="ArgumentException"><paramref name="role"/> is <see cref="MerchantRole.MerchantOwner"/>.</exception>
    public static Membership CreateMember(Guid tenantId, SubjectId subject, MerchantRole role, DateTimeOffset now)
    {
        if (role == MerchantRole.MerchantOwner)
        {
            throw new ArgumentException("MerchantOwner is granted only by tenant provisioning.", nameof(role));
        }

        return Create(tenantId, subject, role, now);
    }

    /// <summary>
    /// Changes the role of an active membership. <paramref name="activeOwnerCount"/> is the tenant's number of active
    /// owner memberships including this one, read under the caller's concurrency control.
    /// </summary>
    public MembershipChangeOutcome ChangeRole(MerchantRole role, int activeOwnerCount, DateTimeOffset now)
    {
        if (role == MerchantRole.MerchantOwner)
        {
            return MembershipChangeOutcome.OwnerRoleReserved;
        }

        if (Status != MembershipStatus.Active)
        {
            return MembershipChangeOutcome.InvalidTransition;
        }

        if (role == Role)
        {
            return MembershipChangeOutcome.Unchanged;
        }

        if (MembershipRules.IsLastActiveOwner(this, activeOwnerCount))
        {
            return MembershipChangeOutcome.LastActiveOwner;
        }

        Role = role;
        return MembershipChangeOutcome.Changed;
    }

    /// <summary>Suspends an active membership. <paramref name="activeOwnerCount"/> as for <see cref="ChangeRole"/>.</summary>
    public MembershipChangeOutcome Suspend(int activeOwnerCount, DateTimeOffset now) =>
        TransitionTo(MembershipStatus.Suspended, activeOwnerCount, now);

    /// <summary>Reactivates a suspended membership. A removed membership never comes back.</summary>
    public MembershipChangeOutcome Reactivate(DateTimeOffset now) =>
        TransitionTo(MembershipStatus.Active, activeOwnerCount: 0, now);

    /// <summary>Removes the membership for good. <paramref name="activeOwnerCount"/> as for <see cref="ChangeRole"/>.</summary>
    public MembershipChangeOutcome Remove(int activeOwnerCount, DateTimeOffset now) =>
        TransitionTo(MembershipStatus.Removed, activeOwnerCount, now);

    private MembershipChangeOutcome TransitionTo(MembershipStatus target, int activeOwnerCount, DateTimeOffset now)
    {
        if (Status == target)
        {
            return MembershipChangeOutcome.Unchanged;
        }

        if (!MembershipRules.CanTransition(Status, target))
        {
            return MembershipChangeOutcome.InvalidTransition;
        }

        if (target != MembershipStatus.Active && MembershipRules.IsLastActiveOwner(this, activeOwnerCount))
        {
            return MembershipChangeOutcome.LastActiveOwner;
        }

        Status = target;
        StatusChangedAt = now;
        return MembershipChangeOutcome.Changed;
    }

    private static Membership Create(Guid tenantId, SubjectId subject, MerchantRole role, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A membership belongs to a tenant.", nameof(tenantId));
        }

        return new Membership
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            SubjectId = subject.Value,
            Role = role,
            Status = MembershipStatus.Active,
            CreatedAt = now,
            StatusChangedAt = now,
        };
    }
}

/// <summary>Membership business rules that do not belong to a single membership.</summary>
public static class MembershipRules
{
    /// <summary>The allowed status transitions: Active ⇄ Suspended, and Active or Suspended → Removed.</summary>
    public static bool CanTransition(MembershipStatus from, MembershipStatus to) => (from, to) switch
    {
        (MembershipStatus.Active, MembershipStatus.Suspended) => true,
        (MembershipStatus.Suspended, MembershipStatus.Active) => true,
        (MembershipStatus.Active, MembershipStatus.Removed) => true,
        (MembershipStatus.Suspended, MembershipStatus.Removed) => true,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="membership"/> is the tenant's last active owner. A tenant must never be left without one,
    /// so such a membership cannot be suspended, removed or demoted.
    /// </summary>
    public static bool IsLastActiveOwner(Membership membership, int activeOwnerCount)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return membership.Role == MerchantRole.MerchantOwner
            && membership.Status == MembershipStatus.Active
            && activeOwnerCount <= 1;
    }
}
