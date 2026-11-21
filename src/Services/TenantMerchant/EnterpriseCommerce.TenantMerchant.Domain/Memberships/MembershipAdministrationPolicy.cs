namespace EnterpriseCommerce.TenantMerchant.Domain.Memberships;

/// <summary>A change of one membership by another member of the same tenant.</summary>
public enum MembershipAdministrationAction
{
    Suspend,
    Reactivate,
    Remove,
    ChangeRole,
}

/// <summary>The policy decision for a membership administration request.</summary>
public enum AdministrationDecision
{
    Allowed,

    /// <summary>The actor's role may not perform the action on this target.</summary>
    Forbidden,

    /// <summary>The actor targets its own membership. Never allowed: no caller changes its own role or status.</summary>
    SelfModification,
}

/// <summary>
/// The merchant role policy for invitations and membership administration (ADR-021, PHASE_3_S2 section 3). Least
/// privilege: staff administer nothing, an admin administers staff only, and an owner administers the organization.
/// <see cref="MerchantRole.MerchantOwner"/> is never granted here; only trusted tenant provisioning creates it.
/// </summary>
/// <remarks>
/// The policy decides <i>who may try</i>. The membership's own rules then decide whether the change is valid: the
/// status transitions, and that the tenant keeps an active owner (<see cref="MembershipRules"/>).
/// </remarks>
public static class MembershipAdministrationPolicy
{
    /// <summary>Whether <paramref name="actor"/> may invite someone with <paramref name="invitedRole"/>.</summary>
    public static bool CanInvite(MerchantRole actor, MerchantRole invitedRole) => (actor, invitedRole) switch
    {
        (MerchantRole.MerchantOwner, MerchantRole.MerchantAdmin or MerchantRole.MerchantStaff) => true,
        (MerchantRole.MerchantAdmin, MerchantRole.MerchantStaff) => true,
        _ => false,
    };

    /// <summary>
    /// Whether <paramref name="actor"/> may cancel an invitation for <paramref name="invitedRole"/>: the same rule as
    /// inviting, so an admin cannot cancel an invitation that only an owner could create.
    /// </summary>
    public static bool CanCancelInvitation(MerchantRole actor, MerchantRole invitedRole) => CanInvite(actor, invitedRole);

    /// <summary>
    /// Decides whether <paramref name="actor"/> may apply <paramref name="action"/> to <paramref name="target"/>. Both
    /// memberships must be of the same tenant, and the actor must be active; the caller guarantees both.
    /// </summary>
    /// <param name="actor">The calling member's membership.</param>
    /// <param name="target">The membership to change.</param>
    /// <param name="action">The change.</param>
    /// <param name="newRole">For <see cref="MembershipAdministrationAction.ChangeRole"/>: the requested role.</param>
    public static AdministrationDecision Decide(Membership actor, Membership target, MembershipAdministrationAction action, MerchantRole? newRole = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(target);
        if (actor.TenantId != target.TenantId)
        {
            throw new ArgumentException("Administration is tenant-scoped.", nameof(target));
        }

        if (actor.Id == target.Id || string.Equals(actor.SubjectId, target.SubjectId, StringComparison.Ordinal))
        {
            return AdministrationDecision.SelfModification;
        }

        return Allows(actor.Role, target.Role, action, newRole) ? AdministrationDecision.Allowed : AdministrationDecision.Forbidden;
    }

    /// <summary>The role matrix itself, without the self-modification rule.</summary>
    public static bool Allows(MerchantRole actor, MerchantRole target, MembershipAdministrationAction action, MerchantRole? newRole = null) =>
        action switch
        {
            MembershipAdministrationAction.Suspend
                or MembershipAdministrationAction.Reactivate
                or MembershipAdministrationAction.Remove => actor switch
                {
                    MerchantRole.MerchantOwner => true,
                    MerchantRole.MerchantAdmin => target == MerchantRole.MerchantStaff,
                    _ => false,
                },

            // Only an owner changes roles, and never to owner (OwnerRoleReserved is answered by the membership itself,
            // whoever asks). An owner may demote another owner; the last-owner rule still applies.
            MembershipAdministrationAction.ChangeRole => actor == MerchantRole.MerchantOwner && newRole is not null,
            _ => false,
        };
}
