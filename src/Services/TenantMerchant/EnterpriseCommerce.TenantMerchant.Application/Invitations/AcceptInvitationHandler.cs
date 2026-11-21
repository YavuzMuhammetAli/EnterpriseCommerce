using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.TenantMerchant.Application.Invitations;

/// <summary>The outcome of an invitation acceptance.</summary>
public enum AcceptInvitationOutcome
{
    /// <summary>The membership was created (<c>201</c>).</summary>
    Accepted,

    /// <summary>The caller had already accepted this invitation; its membership is returned (<c>200</c>).</summary>
    Replayed,

    /// <summary>No token was sent (<c>400</c>).</summary>
    Invalid,

    /// <summary>No invitation has this token (<c>404</c>).</summary>
    NotFound,

    /// <summary>The invitation was cancelled, or accepted by someone else (<c>409</c>).</summary>
    NotPending,

    /// <summary>The invitation expired (<c>410</c>).</summary>
    Expired,

    /// <summary>The caller's verified email is not the invited address (<c>403</c>).</summary>
    EmailMismatch,

    /// <summary>The caller's email is not verified (<c>403</c>).</summary>
    EmailUnverified,

    /// <summary>The caller's account is not active, or unknown to Identity (<c>403</c>).</summary>
    AccountIneligible,

    /// <summary>The caller already has a current (active or suspended) membership of the tenant (<c>409</c>).</summary>
    AlreadyMember,

    /// <summary>A concurrent change won; nothing was done (<c>409</c>).</summary>
    Conflict,
}

/// <summary>The result of an invitation acceptance.</summary>
public sealed record AcceptInvitationResult(AcceptInvitationOutcome Outcome, Guid? TenantId = null, MembershipView? Membership = null);

/// <summary>A request to accept an invitation, by the authenticated caller.</summary>
/// <param name="Token">The acceptance token from the invitation (request body). Never logged.</param>
/// <param name="Caller">The caller; the membership is created for this subject and no other.</param>
public sealed record AcceptInvitationCommand(string? Token, MemberCaller Caller)
{
    /// <summary>Never renders the token.</summary>
    public override string ToString() => $"{nameof(AcceptInvitationCommand)} {{ Caller = {Caller.SubjectId} }}";
}

/// <summary>
/// Accepts an invitation for the authenticated caller (ADR-021, PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.1.2).
/// </summary>
/// <remarks>
/// <para>
/// The token proves that the caller received the invitation; Identity then confirms that the caller's <b>verified</b>
/// email is the invited address (decision D14). The subject is always the access token's <c>sub</c>, and the tenant is
/// always the invitation's own: nothing in the request chooses either. Acceptance never creates an Identity account.
/// </para>
/// <para>
/// Identity is asked <b>before</b> the tenant lock is taken, so that no lock waits on a remote call. Inside the lock the
/// invitation is read again: of concurrent acceptances exactly one creates the membership, and the others see an
/// accepted invitation. Identity unavailable means no change (<see cref="IdentityUnavailableException"/>, <c>503</c>).
/// </para>
/// </remarks>
public sealed partial class AcceptInvitationHandler(
    ITenantWriteGate gate,
    IInvitationReadStore invitations,
    ITenantReadStore tenants,
    IIdentityAccountVerifier identity,
    TimeProvider timeProvider,
    ILogger<AcceptInvitationHandler> logger)
{
    public const string Operation = "invitation-accept";

    public async Task<AcceptInvitationResult> HandleAsync(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrEmpty(command.Token))
        {
            return new AcceptInvitationResult(AcceptInvitationOutcome.Invalid);
        }

        if (!SubjectId.TryCreate(command.Caller.SubjectId, out var subject)
            || !InvitationToken.TryHash(command.Token, out var tokenHash)
            || await invitations.FindByTokenHashAsync(tokenHash, cancellationToken) is not { } found)
        {
            return Refused(AcceptInvitationOutcome.NotFound, command.Caller.SubjectId, invitationId: null);
        }

        var now = timeProvider.GetUtcNow();
        switch (found.Effective(now).Status)
        {
            case InvitationStatus.Accepted:
                return await ReplayOrRefuseAsync(found, subject, cancellationToken);
            case InvitationStatus.Cancelled:
                return Refused(AcceptInvitationOutcome.NotPending, subject.Value, found.Id);
            case InvitationStatus.Expired:
                await RecordExpiryAsync(found, command.Caller.CorrelationId, now, cancellationToken);
                return Refused(AcceptInvitationOutcome.Expired, subject.Value, found.Id);
        }

        var check = await identity.CheckEmailAsync(subject.Value, found.Email, cancellationToken);
        if (check != IdentityEmailCheck.Match)
        {
            return Refused(
                check switch
                {
                    IdentityEmailCheck.Mismatch => AcceptInvitationOutcome.EmailMismatch,
                    IdentityEmailCheck.Unverified => AcceptInvitationOutcome.EmailUnverified,
                    _ => AcceptInvitationOutcome.AccountIneligible,
                },
                subject.Value,
                found.Id);
        }

        AcceptInvitationResult result;
        try
        {
            result = await gate.ExecuteAsync(
                Operation,
                found.TenantId,
                async (session, ct) =>
                {
                    if (!session.TenantExists || await session.FindInvitationAsync(found.Id, ct) is not { } invitation)
                    {
                        return WriteDecision.Rollback(new AcceptInvitationResult(AcceptInvitationOutcome.NotFound));
                    }

                    if (invitation.Status == InvitationStatus.Accepted)
                    {
                        // A concurrent acceptance committed first.
                        return WriteDecision.Rollback(
                            string.Equals(invitation.AcceptedBySubjectId, subject.Value, StringComparison.Ordinal)
                                ? new AcceptInvitationResult(
                                    AcceptInvitationOutcome.Replayed,
                                    invitation.TenantId,
                                    await session.FindMembershipAsync(invitation.AcceptedMembershipId!.Value, ct) is { } accepted ? MembershipView.Of(accepted) : null)
                                : new AcceptInvitationResult(AcceptInvitationOutcome.NotPending));
                    }

                    if (invitation.Status != InvitationStatus.Pending)
                    {
                        return WriteDecision.Rollback(new AcceptInvitationResult(
                            invitation.Status == InvitationStatus.Expired ? AcceptInvitationOutcome.Expired : AcceptInvitationOutcome.NotPending));
                    }

                    if (invitation.IsPastExpiry(now))
                    {
                        invitation.MarkExpired(now);
                        session.Add(MembershipAuditRecord.InvitationClosed(
                            MembershipAuditAction.InvitationExpired, invitation, InvitationStatus.Pending, AuditActor.System, command.Caller.CorrelationId, now));
                        return WriteDecision.Commit(new AcceptInvitationResult(AcceptInvitationOutcome.Expired));
                    }

                    if (await session.FindCurrentMembershipAsync(subject.Value, ct) is not null)
                    {
                        return WriteDecision.Rollback(new AcceptInvitationResult(AcceptInvitationOutcome.AlreadyMember));
                    }

                    var membership = Membership.CreateMember(invitation.TenantId, subject, invitation.Role, now);
                    if (invitation.Accept(subject, membership.Id, now) != InvitationChangeOutcome.Changed)
                    {
                        return WriteDecision.Rollback(new AcceptInvitationResult(AcceptInvitationOutcome.NotPending));
                    }

                    session.Add(membership);
                    session.Add(MembershipAuditRecord.InvitationAccepted(invitation, membership, AuditActor.User(subject), command.Caller.CorrelationId, now));
                    return WriteDecision.Commit(new AcceptInvitationResult(AcceptInvitationOutcome.Accepted, invitation.TenantId, MembershipView.Of(membership)));
                },
                cancellationToken);
        }
        catch (TenantWriteConflictException exception)
        {
            // The filtered unique index on current memberships: the subject joined the tenant by another path meanwhile.
            result = new AcceptInvitationResult(
                exception.Conflict == TenantWriteConflict.UniqueConstraint ? AcceptInvitationOutcome.AlreadyMember : AcceptInvitationOutcome.Conflict);
        }

        if (result.Outcome == AcceptInvitationOutcome.Accepted)
        {
            LogInvitationAccepted(logger, found.Id, found.TenantId, subject.Value, result.Membership!.Id);
            return result;
        }

        return result.Outcome == AcceptInvitationOutcome.Replayed ? result : Refused(result.Outcome, subject.Value, found.Id);
    }

    private async Task<AcceptInvitationResult> ReplayOrRefuseAsync(InvitationView found, SubjectId subject, CancellationToken cancellationToken)
    {
        if (!string.Equals(found.AcceptedBySubjectId, subject.Value, StringComparison.Ordinal) || found.AcceptedMembershipId is not { } membershipId)
        {
            return Refused(AcceptInvitationOutcome.NotPending, subject.Value, found.Id);
        }

        // The caller retries an acceptance it already made: the same membership, in its current state.
        var membership = await tenants.GetMembershipAsync(found.TenantId, membershipId, cancellationToken);
        return new AcceptInvitationResult(AcceptInvitationOutcome.Replayed, found.TenantId, membership);
    }

    /// <summary>Records the lapse of a pending invitation (with its audit record) the first time someone presents it.</summary>
    private async Task RecordExpiryAsync(InvitationView found, string? correlationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (found.Status != InvitationStatus.Pending)
        {
            return;
        }

        await gate.ExecuteAsync(
            Operation,
            found.TenantId,
            async (session, ct) =>
            {
                if (session.TenantExists
                    && await session.FindInvitationAsync(found.Id, ct) is { } invitation
                    && invitation.MarkExpired(now) == InvitationChangeOutcome.Changed)
                {
                    session.Add(MembershipAuditRecord.InvitationClosed(
                        MembershipAuditAction.InvitationExpired, invitation, InvitationStatus.Pending, AuditActor.System, correlationId, now));
                    return WriteDecision.Commit(true);
                }

                return WriteDecision.Rollback(false);
            },
            cancellationToken);
    }

    private AcceptInvitationResult Refused(AcceptInvitationOutcome outcome, string subject, Guid? invitationId)
    {
        LogAcceptanceRefused(logger, outcome, subject, invitationId);
        return new AcceptInvitationResult(outcome);
    }

    [LoggerMessage(
        EventId = 3202,
        Level = LogLevel.Information,
        Message = "Invitation {InvitationId} of tenant {TenantId} accepted by subject {Subject}, creating membership {MembershipId}")]
    private static partial void LogInvitationAccepted(ILogger logger, Guid invitationId, Guid tenantId, string subject, Guid membershipId);

    [LoggerMessage(
        EventId = 3203,
        Level = LogLevel.Warning,
        Message = "Invitation acceptance refused ({Outcome}) for subject {Subject}, invitation {InvitationId}")]
    private static partial void LogAcceptanceRefused(ILogger logger, AcceptInvitationOutcome outcome, string subject, Guid? invitationId);
}
