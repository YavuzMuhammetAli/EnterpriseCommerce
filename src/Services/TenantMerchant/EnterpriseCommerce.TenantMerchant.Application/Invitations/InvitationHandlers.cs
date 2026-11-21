using EnterpriseCommerce.TenantMerchant.Application.Authorization;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.TenantMerchant.Application.Invitations;

/// <summary>The outcome of an invitation command.</summary>
public enum InvitationCommandOutcome
{
    Created,
    Cancelled,

    /// <summary>The invitation was already cancelled. Nothing changed.</summary>
    Unchanged,

    /// <summary>The request is invalid; see the errors.</summary>
    Invalid,

    /// <summary>The caller is not an active member, or the tenant does not exist (<c>404</c>).</summary>
    TenantNotVisible,

    /// <summary>The caller's role may not do this (<c>403</c>).</summary>
    PermissionDenied,

    /// <summary>An invitation can never be for <see cref="MerchantRole.MerchantOwner"/> (<c>422</c>).</summary>
    OwnerRoleReserved,

    /// <summary>The address already has a pending invitation in this tenant (<c>409</c>).</summary>
    AlreadyPending,

    /// <summary>The tenant has the maximum number of open invitations (<c>409</c>).</summary>
    LimitReached,

    /// <summary>No such invitation in this tenant (<c>404</c>).</summary>
    InvitationNotFound,

    /// <summary>The invitation had lapsed; its expiry is now recorded (<c>410</c>).</summary>
    Expired,

    /// <summary>The invitation was accepted or expired before; it cannot be cancelled (<c>409</c>).</summary>
    InvalidTransition,
}

/// <summary>The result of an invitation command.</summary>
/// <param name="AcceptanceToken">
/// Set only when an invitation was created: the raw single-use token, returned <b>once</b> to the inviter (ADR-021).
/// It is never stored, logged or returned again.
/// </param>
public sealed record InvitationCommandResult(
    InvitationCommandOutcome Outcome,
    InvitationView? Invitation = null,
    string? AcceptanceToken = null,
    Guid? ExistingInvitationId = null,
    IReadOnlyDictionary<string, string[]>? Errors = null)
{
    /// <summary>Never renders the acceptance token.</summary>
    public override string ToString() => $"{nameof(InvitationCommandResult)} {{ Outcome = {Outcome}, Invitation = {Invitation?.Id} }}";
}

/// <summary>A request to invite an address into the caller's tenant with a non-owner role.</summary>
public sealed record CreateInvitationCommand(Guid TenantId, string? Email, string? Role, MemberCaller Caller);

/// <summary>
/// Creates an invitation (ADR-021): owners invite admins and staff, admins invite staff, staff invite nobody, and nobody
/// invites an owner. At most one pending invitation per address and tenant; a lapsed one is closed first. The token is
/// generated here and only its hash is stored.
/// </summary>
public sealed partial class CreateInvitationHandler(
    ITenantWriteGate gate,
    TenantAccessAuthorizer authorizer,
    InvitationSettings settings,
    TimeProvider timeProvider,
    ILogger<CreateInvitationHandler> logger)
{
    public const string Operation = "invitation-create";

    public async Task<InvitationCommandResult> HandleAsync(CreateInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!InviteeEmail.TryCreate(command.Email, out var email))
        {
            errors["email"] = [$"The email must be a plain address of at most {InviteeEmail.MaxLength} characters."];
        }

        if (!MerchantRoles.TryParse(command.Role, out var role))
        {
            errors["role"] = ["The role must be MerchantAdmin or MerchantStaff."];
        }

        if (errors.Count > 0)
        {
            return new InvitationCommandResult(InvitationCommandOutcome.Invalid, Errors: errors);
        }

        if (!SubjectId.TryCreate(command.Caller.SubjectId, out var inviter))
        {
            return new InvitationCommandResult(InvitationCommandOutcome.TenantNotVisible);
        }

        // Generated before the transaction, so that a retried attempt stages the same token hash.
        var (token, tokenHash) = InvitationToken.Generate();
        var now = timeProvider.GetUtcNow();
        var actor = AuditActor.User(inviter);

        try
        {
            var result = await gate.ExecuteAsync(
                Operation,
                command.TenantId,
                async (session, ct) =>
                {
                    var access = await authorizer.AuthorizeAsync(session, command.TenantId, inviter.Value, MerchantPermission.ManageInvitations, ct);
                    if (access.Access != TenantAccess.Allowed)
                    {
                        return WriteDecision.Rollback(new InvitationCommandResult(Map(access.Access)));
                    }

                    if (role == MerchantRole.MerchantOwner)
                    {
                        return WriteDecision.Rollback(new InvitationCommandResult(InvitationCommandOutcome.OwnerRoleReserved));
                    }

                    if (!MembershipAdministrationPolicy.CanInvite(access.Actor!.Role, role))
                    {
                        LogInvitationDenied(logger, inviter.Value, access.Actor.Role, role, command.TenantId);
                        return WriteDecision.Rollback(new InvitationCommandResult(InvitationCommandOutcome.PermissionDenied));
                    }

                    if (await session.FindPendingInvitationAsync(email!.Normalized, ct) is { } pending)
                    {
                        if (pending.MarkExpired(now) != InvitationChangeOutcome.Changed)
                        {
                            return WriteDecision.Rollback(new InvitationCommandResult(
                                InvitationCommandOutcome.AlreadyPending, ExistingInvitationId: pending.Id));
                        }

                        // The lapsed invitation is closed and audited before its replacement is inserted.
                        session.Add(MembershipAuditRecord.InvitationClosed(
                            MembershipAuditAction.InvitationExpired, pending, InvitationStatus.Pending, AuditActor.System, command.Caller.CorrelationId, now));
                        await session.FlushAsync(ct);
                    }

                    if (await session.CountOpenInvitationsAsync(now, ct) >= settings.MaxOpenPerTenant)
                    {
                        return WriteDecision.Rollback(new InvitationCommandResult(InvitationCommandOutcome.LimitReached));
                    }

                    var invitation = MerchantInvitation.Create(command.TenantId, email, role, tokenHash, inviter, now, settings.Lifetime);
                    session.Add(invitation);
                    session.Add(MembershipAuditRecord.InvitationCreated(invitation, actor, command.Caller.CorrelationId, now));
                    return WriteDecision.Commit(new InvitationCommandResult(InvitationCommandOutcome.Created, InvitationView.Of(invitation)));
                },
                cancellationToken);

            if (result.Outcome == InvitationCommandOutcome.Created)
            {
                LogInvitationCreated(logger, result.Invitation!.Id, command.TenantId, role, inviter.Value);
                return result with { AcceptanceToken = token };
            }

            return result;
        }
        catch (TenantWriteConflictException exception) when (exception.Conflict == TenantWriteConflict.UniqueConstraint)
        {
            // The filtered unique index on pending invitations: a backstop, since the tenant lock serializes creation.
            return new InvitationCommandResult(InvitationCommandOutcome.AlreadyPending);
        }
    }

    internal static InvitationCommandOutcome Map(TenantAccess access) => access switch
    {
        TenantAccess.Forbidden => InvitationCommandOutcome.PermissionDenied,
        _ => InvitationCommandOutcome.TenantNotVisible,
    };

    [LoggerMessage(
        EventId = 3200,
        Level = LogLevel.Information,
        Message = "Invitation {InvitationId} created in tenant {TenantId} for role {Role} by subject {Subject}")]
    private static partial void LogInvitationCreated(ILogger logger, Guid invitationId, Guid tenantId, MerchantRole role, string subject);

    [LoggerMessage(
        EventId = 3204,
        Level = LogLevel.Warning,
        Message = "Invitation denied: subject {Subject} with role {ActorRole} may not invite {InvitedRole} in tenant {TenantId}")]
    private static partial void LogInvitationDenied(ILogger logger, string subject, MerchantRole actorRole, MerchantRole invitedRole, Guid tenantId);
}

/// <summary>A request to cancel a pending invitation of the caller's tenant.</summary>
public sealed record CancelInvitationCommand(Guid TenantId, Guid InvitationId, MemberCaller Caller);

/// <summary>
/// Cancels a pending invitation. Whoever may invite a role may cancel invitations for it, so an admin cannot cancel an
/// invitation that only an owner could create. A lapsed invitation is recorded as expired instead.
/// </summary>
public sealed partial class CancelInvitationHandler(
    ITenantWriteGate gate,
    TenantAccessAuthorizer authorizer,
    TimeProvider timeProvider,
    ILogger<CancelInvitationHandler> logger)
{
    public const string Operation = "invitation-cancel";

    public async Task<InvitationCommandResult> HandleAsync(CancelInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!SubjectId.TryCreate(command.Caller.SubjectId, out var caller))
        {
            return new InvitationCommandResult(InvitationCommandOutcome.TenantNotVisible);
        }

        var now = timeProvider.GetUtcNow();
        var result = await gate.ExecuteAsync(
            Operation,
            command.TenantId,
            async (session, ct) =>
            {
                var access = await authorizer.AuthorizeAsync(session, command.TenantId, caller.Value, MerchantPermission.ManageInvitations, ct);
                if (access.Access != TenantAccess.Allowed)
                {
                    return WriteDecision.Rollback(new InvitationCommandResult(CreateInvitationHandler.Map(access.Access)));
                }

                if (await session.FindInvitationAsync(command.InvitationId, ct) is not { } invitation)
                {
                    return WriteDecision.Rollback(new InvitationCommandResult(InvitationCommandOutcome.InvitationNotFound));
                }

                if (!MembershipAdministrationPolicy.CanCancelInvitation(access.Actor!.Role, invitation.Role))
                {
                    return WriteDecision.Rollback(new InvitationCommandResult(InvitationCommandOutcome.PermissionDenied));
                }

                var previous = invitation.Status;
                switch (invitation.Cancel(caller, now))
                {
                    case InvitationChangeOutcome.Changed:
                        session.Add(MembershipAuditRecord.InvitationClosed(
                            MembershipAuditAction.InvitationCancelled, invitation, previous, AuditActor.User(caller), command.Caller.CorrelationId, now));
                        return WriteDecision.Commit(new InvitationCommandResult(InvitationCommandOutcome.Cancelled, InvitationView.Of(invitation)));

                    case InvitationChangeOutcome.Unchanged:
                        return WriteDecision.Rollback(new InvitationCommandResult(InvitationCommandOutcome.Unchanged, InvitationView.Of(invitation)));

                    case InvitationChangeOutcome.Expired:
                        invitation.MarkExpired(now);
                        session.Add(MembershipAuditRecord.InvitationClosed(
                            MembershipAuditAction.InvitationExpired, invitation, previous, AuditActor.System, command.Caller.CorrelationId, now));
                        return WriteDecision.Commit(new InvitationCommandResult(InvitationCommandOutcome.Expired, InvitationView.Of(invitation)));

                    default:
                        return WriteDecision.Rollback(new InvitationCommandResult(
                            invitation.Status == InvitationStatus.Expired ? InvitationCommandOutcome.Expired : InvitationCommandOutcome.InvalidTransition,
                            InvitationView.Of(invitation)));
                }
            },
            cancellationToken);

        if (result.Outcome == InvitationCommandOutcome.Cancelled)
        {
            LogInvitationCancelled(logger, command.InvitationId, command.TenantId, caller.Value);
        }

        return result;
    }

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Information,
        Message = "Invitation {InvitationId} in tenant {TenantId} cancelled by subject {Subject}")]
    private static partial void LogInvitationCancelled(ILogger logger, Guid invitationId, Guid tenantId, string subject);
}

/// <summary>The result of an invitation read.</summary>
public sealed record InvitationReadResult(TenantAccess Access, InvitationView? Invitation = null, InvitationPage? Page = null);

/// <summary>Reads a tenant's invitations (<see cref="MerchantPermission.ViewInvitations"/>). Tokens are never part of a read.</summary>
public sealed class InvitationQueryHandler(TenantAccessAuthorizer authorizer, IInvitationReadStore store, TimeProvider timeProvider)
{
    public async Task<InvitationReadResult> GetAsync(Guid tenantId, Guid invitationId, string subjectId, CancellationToken cancellationToken)
    {
        var decision = await authorizer.AuthorizeAsync(tenantId, subjectId, MerchantPermission.ViewInvitations, cancellationToken);
        if (decision.Access != TenantAccess.Allowed)
        {
            return new InvitationReadResult(decision.Access);
        }

        var invitation = await store.GetAsync(tenantId, invitationId, cancellationToken);
        return new InvitationReadResult(TenantAccess.Allowed, invitation?.Effective(timeProvider.GetUtcNow()));
    }

    public async Task<InvitationReadResult> ListAsync(
        Guid tenantId,
        InvitationStatus? status,
        string subjectId,
        PageRequest page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        var decision = await authorizer.AuthorizeAsync(tenantId, subjectId, MerchantPermission.ViewInvitations, cancellationToken);
        if (decision.Access != TenantAccess.Allowed)
        {
            return new InvitationReadResult(decision.Access);
        }

        var now = timeProvider.GetUtcNow();
        var invitations = await store.ListAsync(tenantId, status, now, page.Skip, page.PageSize, cancellationToken);
        return new InvitationReadResult(
            TenantAccess.Allowed,
            Page: invitations with { Page = page.Page, Items = [.. invitations.Items.Select(i => i.Effective(now))] });
    }
}

/// <summary>Strict parsing of merchant role names from requests: the exact names only, never numbers.</summary>
public static class MerchantRoles
{
    public static bool TryParse(string? value, out MerchantRole role)
    {
        role = default;
        return value is not null
            && Enum.GetNames<MerchantRole>().Contains(value, StringComparer.Ordinal)
            && Enum.TryParse(value, ignoreCase: false, out role);
    }
}
