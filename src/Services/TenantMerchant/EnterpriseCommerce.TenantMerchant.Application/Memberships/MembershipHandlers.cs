using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.TenantMerchant.Application.Memberships;

/// <summary>The outcome of a membership administration command.</summary>
public enum MembershipCommandOutcome
{
    Changed,

    /// <summary>The membership already had the requested state. Nothing was written (<c>200</c>).</summary>
    Unchanged,

    /// <summary>The request is invalid; see the errors (<c>400</c>).</summary>
    Invalid,

    /// <summary>The caller is not an active member, or the tenant does not exist (<c>404</c>).</summary>
    TenantNotVisible,

    /// <summary>The caller's role may not do this to this membership (<c>403</c>).</summary>
    PermissionDenied,

    /// <summary>The caller targets its own membership (<c>403</c>).</summary>
    SelfModification,

    /// <summary>No such membership in this tenant (<c>404</c>).</summary>
    MembershipNotFound,

    /// <summary>The status transition is not allowed, for example out of <c>Removed</c> (<c>409</c>).</summary>
    InvalidTransition,

    /// <summary>The change would leave the tenant without an active owner (<c>409</c>).</summary>
    LastActiveOwner,

    /// <summary><see cref="MerchantRole.MerchantOwner"/> is never assigned by a role change (<c>422</c>).</summary>
    OwnerRoleReserved,

    /// <summary>A concurrent change won; nothing was done (<c>409</c>).</summary>
    Conflict,
}

/// <summary>The result of a membership administration command.</summary>
public sealed record MembershipCommandResult(
    MembershipCommandOutcome Outcome,
    MembershipView? Membership = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);

/// <summary>A request to suspend, reactivate or remove a membership of the caller's tenant.</summary>
public sealed record ChangeMembershipStatusCommand(Guid TenantId, Guid MembershipId, MembershipAdministrationAction Action, MemberCaller Caller);

/// <summary>A request to change the role of a membership of the caller's tenant.</summary>
public sealed record ChangeMembershipRoleCommand(Guid TenantId, Guid MembershipId, string? Role, MemberCaller Caller);

/// <summary>
/// Suspends, reactivates, removes and re-roles memberships (ADR-021). Every change runs in the tenant's serialized
/// write transaction: the caller is re-authorized under the lock, the active owners are counted under the lock, and the
/// change commits together with its audit record. So two concurrent changes can never both pass the last-owner rule on
/// a stale count, and a failed audit write fails the change.
/// </summary>
public sealed partial class MembershipAdministrationHandler(
    ITenantWriteGate gate,
    TenantAccessAuthorizer authorizer,
    TimeProvider timeProvider,
    ILogger<MembershipAdministrationHandler> logger)
{
    public const string StatusOperation = "membership-status";
    public const string RoleOperation = "membership-role";

    public Task<MembershipCommandResult> HandleAsync(ChangeMembershipStatusCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Action == MembershipAdministrationAction.ChangeRole)
        {
            throw new ArgumentException("Role changes use ChangeMembershipRoleCommand.", nameof(command));
        }

        return ExecuteAsync(
            StatusOperation,
            command.TenantId,
            command.MembershipId,
            command.Action,
            newRole: null,
            MerchantPermission.ManageMemberships,
            command.Caller,
            cancellationToken);
    }

    public Task<MembershipCommandResult> HandleAsync(ChangeMembershipRoleCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!MerchantRoles.TryParse(command.Role, out var role))
        {
            return Task.FromResult(new MembershipCommandResult(
                MembershipCommandOutcome.Invalid,
                Errors: new Dictionary<string, string[]>(StringComparer.Ordinal) { ["role"] = ["The role must be MerchantAdmin or MerchantStaff."] }));
        }

        return ExecuteAsync(
            RoleOperation,
            command.TenantId,
            command.MembershipId,
            MembershipAdministrationAction.ChangeRole,
            role,
            MerchantPermission.ChangeRoles,
            command.Caller,
            cancellationToken);
    }

    private async Task<MembershipCommandResult> ExecuteAsync(
        string operation,
        Guid tenantId,
        Guid membershipId,
        MembershipAdministrationAction action,
        MerchantRole? newRole,
        MerchantPermission permission,
        MemberCaller caller,
        CancellationToken cancellationToken)
    {
        if (!SubjectId.TryCreate(caller.SubjectId, out var subject))
        {
            return new MembershipCommandResult(MembershipCommandOutcome.TenantNotVisible);
        }

        var now = timeProvider.GetUtcNow();
        var actor = AuditActor.User(subject);
        MembershipCommandResult result;
        try
        {
            result = await gate.ExecuteAsync(
                operation,
                tenantId,
                async (session, ct) =>
                {
                    var access = await authorizer.AuthorizeAsync(session, tenantId, subject.Value, permission, ct);
                    if (access.Access != TenantAccess.Allowed)
                    {
                        return WriteDecision.Rollback(new MembershipCommandResult(
                            access.Access == TenantAccess.Forbidden ? MembershipCommandOutcome.PermissionDenied : MembershipCommandOutcome.TenantNotVisible));
                    }

                    if (await session.FindMembershipAsync(membershipId, ct) is not { } target)
                    {
                        return WriteDecision.Rollback(new MembershipCommandResult(MembershipCommandOutcome.MembershipNotFound));
                    }

                    switch (MembershipAdministrationPolicy.Decide(access.Actor!, target, action, newRole))
                    {
                        case AdministrationDecision.SelfModification:
                            LogAdministrationDenied(logger, subject.Value, action, access.Actor!.Role, target.Role, tenantId, "self");
                            return WriteDecision.Rollback(new MembershipCommandResult(MembershipCommandOutcome.SelfModification));
                        case AdministrationDecision.Forbidden:
                            LogAdministrationDenied(logger, subject.Value, action, access.Actor!.Role, target.Role, tenantId, "role");
                            return WriteDecision.Rollback(new MembershipCommandResult(MembershipCommandOutcome.PermissionDenied));
                    }

                    var previousRole = target.Role;
                    var previousStatus = target.Status;
                    var activeOwners = await session.CountActiveOwnersAsync(ct);
                    var outcome = action switch
                    {
                        MembershipAdministrationAction.Suspend => target.Suspend(activeOwners, now),
                        MembershipAdministrationAction.Reactivate => target.Reactivate(now),
                        MembershipAdministrationAction.Remove => target.Remove(activeOwners, now),
                        _ => target.ChangeRole(newRole!.Value, activeOwners, now),
                    };

                    if (outcome != MembershipChangeOutcome.Changed)
                    {
                        return WriteDecision.Rollback(new MembershipCommandResult(Map(outcome), MembershipView.Of(target)));
                    }

                    session.Add(action == MembershipAdministrationAction.ChangeRole
                        ? MembershipAuditRecord.MembershipRoleChanged(target, previousRole, actor, caller.CorrelationId, now)
                        : MembershipAuditRecord.MembershipStatusChanged(target, previousStatus, actor, caller.CorrelationId, now));
                    return WriteDecision.Commit(new MembershipCommandResult(MembershipCommandOutcome.Changed, MembershipView.Of(target)));
                },
                cancellationToken);
        }
        catch (TenantWriteConflictException exception)
        {
            result = new MembershipCommandResult(exception.Conflict == TenantWriteConflict.LastActiveOwner
                ? MembershipCommandOutcome.LastActiveOwner
                : MembershipCommandOutcome.Conflict);
        }

        switch (result.Outcome)
        {
            case MembershipCommandOutcome.Changed:
                LogMembershipChanged(logger, result.Membership!.Id, tenantId, action, result.Membership.Role, result.Membership.Status, subject.Value);
                break;
            case MembershipCommandOutcome.LastActiveOwner:
                LogLastActiveOwnerProtected(logger, membershipId, tenantId, action, subject.Value);
                break;
        }

        return result;
    }

    private static MembershipCommandOutcome Map(MembershipChangeOutcome outcome) => outcome switch
    {
        MembershipChangeOutcome.Unchanged => MembershipCommandOutcome.Unchanged,
        MembershipChangeOutcome.InvalidTransition => MembershipCommandOutcome.InvalidTransition,
        MembershipChangeOutcome.OwnerRoleReserved => MembershipCommandOutcome.OwnerRoleReserved,
        MembershipChangeOutcome.LastActiveOwner => MembershipCommandOutcome.LastActiveOwner,
        _ => MembershipCommandOutcome.Changed,
    };

    [LoggerMessage(
        EventId = 3210,
        Level = LogLevel.Information,
        Message = "Membership {MembershipId} of tenant {TenantId} changed ({Action}) to role {Role} and status {Status} by subject {Subject}")]
    private static partial void LogMembershipChanged(
        ILogger logger, Guid membershipId, Guid tenantId, MembershipAdministrationAction action, MerchantRole role, MembershipStatus status, string subject);

    [LoggerMessage(
        EventId = 3211,
        Level = LogLevel.Warning,
        Message = "Membership administration denied ({Reason}): subject {Subject} with role {ActorRole} may not {Action} a {TargetRole} in tenant {TenantId}")]
    private static partial void LogAdministrationDenied(
        ILogger logger, string subject, MembershipAdministrationAction action, MerchantRole actorRole, MerchantRole targetRole, Guid tenantId, string reason);

    [LoggerMessage(
        EventId = 3212,
        Level = LogLevel.Warning,
        Message = "Membership {MembershipId} of tenant {TenantId} kept: {Action} by subject {Subject} would leave no active owner")]
    private static partial void LogLastActiveOwnerProtected(ILogger logger, Guid membershipId, Guid tenantId, MembershipAdministrationAction action, string subject);
}

/// <summary>The result of a single-membership read.</summary>
public sealed record MembershipReadResult(TenantAccess Access, MembershipView? Membership);

/// <summary>Reads one membership of a tenant (<see cref="MerchantPermission.ViewMemberships"/>), in any status.</summary>
public sealed class GetMembershipHandler(TenantAccessAuthorizer authorizer, ITenantReadStore store)
{
    public async Task<MembershipReadResult> HandleAsync(Guid tenantId, Guid membershipId, string subjectId, CancellationToken cancellationToken)
    {
        var decision = await authorizer.AuthorizeAsync(tenantId, subjectId, MerchantPermission.ViewMemberships, cancellationToken);
        return decision.Access != TenantAccess.Allowed
            ? new MembershipReadResult(decision.Access, null)
            : new MembershipReadResult(TenantAccess.Allowed, await store.GetMembershipAsync(tenantId, membershipId, cancellationToken));
    }
}

/// <summary>
/// Lists the tenants in which the caller is an active member: the caller's own data only, so no permission applies.
/// A suspended or removed membership is not listed, as it grants nothing.
/// </summary>
public sealed class ListCallerTenantsHandler(ITenantReadStore store)
{
    public async Task<CallerTenantPage> HandleAsync(string subjectId, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(subjectId);
        ArgumentNullException.ThrowIfNull(page);

        var tenants = await store.ListCallerTenantsAsync(subjectId, page.Skip, page.PageSize, cancellationToken);
        return tenants with { Page = page.Page };
    }
}
