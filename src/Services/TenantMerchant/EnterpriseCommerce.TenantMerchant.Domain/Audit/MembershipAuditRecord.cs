using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.Domain.Audit;

/// <summary>The security-sensitive membership operations that are audited (ADR-021).</summary>
public enum MembershipAuditAction
{
    OwnerMembershipProvisioned,
    InvitationCreated,
    InvitationCancelled,
    InvitationAccepted,
    InvitationExpired,
    MembershipSuspended,
    MembershipReactivated,
    MembershipRemoved,
    MembershipRoleChanged,
    SelfServiceOwnerMembershipCreated,
}

/// <summary>Who caused an audited change.</summary>
public enum AuditActorKind
{
    /// <summary>A merchant-portal user; the actor ID is the Identity subject.</summary>
    User,

    /// <summary>A trusted service client; the actor ID is its <c>client_id</c>.</summary>
    Service,

    /// <summary>The service itself, for example recording that an invitation expired.</summary>
    System,
}

/// <summary>The actor of an audited change.</summary>
public sealed record AuditActor(AuditActorKind Kind, string Id)
{
    /// <summary>The actor ID of changes the service records itself.</summary>
    public const string SystemId = "tenant-merchant";

    public static AuditActor User(SubjectId subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new AuditActor(AuditActorKind.User, subject.Value);
    }

    public static AuditActor Service(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return new AuditActor(AuditActorKind.Service, clientId);
    }

    public static AuditActor System { get; } = new(AuditActorKind.System, SystemId);
}

/// <summary>
/// One append-only audit record of a membership or invitation change, written in the same transaction as the change,
/// so that a failed audit write fails the change (ADR-021). It holds identifiers and non-sensitive before and after
/// state only: never an email address, a token, a token hash or a credential.
/// </summary>
public sealed class MembershipAuditRecord
{
    /// <summary>The longest correlation ID kept (the <c>X-Correlation-ID</c> limit).</summary>
    public const int CorrelationIdMaxLength = 64;

    private MembershipAuditRecord()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public MembershipAuditAction Action { get; private set; }

    public AuditActorKind ActorKind { get; private set; }

    public string ActorId { get; private set; } = string.Empty;

    public Guid? MembershipId { get; private set; }

    public Guid? InvitationId { get; private set; }

    /// <summary>The subject of the membership concerned, when there is one.</summary>
    public string? SubjectId { get; private set; }

    public string? PreviousRole { get; private set; }

    public string? NewRole { get; private set; }

    public string? PreviousStatus { get; private set; }

    public string? NewStatus { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>The initial owner membership, created by tenant provisioning.</summary>
    public static MembershipAuditRecord OwnerProvisioned(Membership owner, AuditActor actor, string? correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return Create(MembershipAuditAction.OwnerMembershipProvisioned, owner.TenantId, actor, correlationId, now) with
        {
            MembershipId = owner.Id,
            SubjectId = owner.SubjectId,
            NewRole = owner.Role.ToString(),
            NewStatus = owner.Status.ToString(),
        };
    }

    /// <summary>The owner membership of a tenant that a merchant-portal user created for themselves (self-service onboarding).</summary>
    public static MembershipAuditRecord SelfServiceOwnerCreated(Membership owner, string? correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!Memberships.SubjectId.TryCreate(owner.SubjectId, out var subject) || owner.Role != MerchantRole.MerchantOwner)
        {
            throw new ArgumentException("A self-service owner record needs an owner membership.", nameof(owner));
        }

        return Create(MembershipAuditAction.SelfServiceOwnerMembershipCreated, owner.TenantId, AuditActor.User(subject), correlationId, now) with
        {
            MembershipId = owner.Id,
            SubjectId = owner.SubjectId,
            NewRole = owner.Role.ToString(),
            NewStatus = owner.Status.ToString(),
        };
    }

    /// <summary>A new invitation. The invited address is not recorded.</summary>
    public static MembershipAuditRecord InvitationCreated(MerchantInvitation invitation, AuditActor actor, string? correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return Create(MembershipAuditAction.InvitationCreated, invitation.TenantId, actor, correlationId, now) with
        {
            InvitationId = invitation.Id,
            NewRole = invitation.Role.ToString(),
            NewStatus = invitation.Status.ToString(),
        };
    }

    /// <summary>An invitation was cancelled or recorded as expired.</summary>
    public static MembershipAuditRecord InvitationClosed(
        MembershipAuditAction action,
        MerchantInvitation invitation,
        InvitationStatus previousStatus,
        AuditActor actor,
        string? correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        if (action is not (MembershipAuditAction.InvitationCancelled or MembershipAuditAction.InvitationExpired))
        {
            throw new ArgumentException("Only cancellation and expiry close an invitation this way.", nameof(action));
        }

        return Create(action, invitation.TenantId, actor, correlationId, now) with
        {
            InvitationId = invitation.Id,
            NewRole = invitation.Role.ToString(),
            PreviousStatus = previousStatus.ToString(),
            NewStatus = invitation.Status.ToString(),
        };
    }

    /// <summary>An invitation was accepted and created <paramref name="membership"/>.</summary>
    public static MembershipAuditRecord InvitationAccepted(
        MerchantInvitation invitation,
        Membership membership,
        AuditActor actor,
        string? correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        ArgumentNullException.ThrowIfNull(membership);
        return Create(MembershipAuditAction.InvitationAccepted, invitation.TenantId, actor, correlationId, now) with
        {
            InvitationId = invitation.Id,
            MembershipId = membership.Id,
            SubjectId = membership.SubjectId,
            NewRole = membership.Role.ToString(),
            PreviousStatus = nameof(InvitationStatus.Pending),
            NewStatus = invitation.Status.ToString(),
        };
    }

    /// <summary>A membership's status changed (suspended, reactivated or removed).</summary>
    public static MembershipAuditRecord MembershipStatusChanged(
        Membership membership,
        MembershipStatus previousStatus,
        AuditActor actor,
        string? correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(membership);
        var action = membership.Status switch
        {
            MembershipStatus.Suspended => MembershipAuditAction.MembershipSuspended,
            MembershipStatus.Active => MembershipAuditAction.MembershipReactivated,
            MembershipStatus.Removed => MembershipAuditAction.MembershipRemoved,
            _ => throw new ArgumentOutOfRangeException(nameof(membership)),
        };

        return Create(action, membership.TenantId, actor, correlationId, now) with
        {
            MembershipId = membership.Id,
            SubjectId = membership.SubjectId,
            PreviousRole = membership.Role.ToString(),
            NewRole = membership.Role.ToString(),
            PreviousStatus = previousStatus.ToString(),
            NewStatus = membership.Status.ToString(),
        };
    }

    /// <summary>A membership's role changed.</summary>
    public static MembershipAuditRecord MembershipRoleChanged(
        Membership membership,
        MerchantRole previousRole,
        AuditActor actor,
        string? correlationId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(membership);
        return Create(MembershipAuditAction.MembershipRoleChanged, membership.TenantId, actor, correlationId, now) with
        {
            MembershipId = membership.Id,
            SubjectId = membership.SubjectId,
            PreviousRole = previousRole.ToString(),
            NewRole = membership.Role.ToString(),
            PreviousStatus = membership.Status.ToString(),
            NewStatus = membership.Status.ToString(),
        };
    }

    private static Draft Create(MembershipAuditAction action, Guid tenantId, AuditActor actor, string? correlationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new Draft(action, tenantId, actor, correlationId is { Length: > 0 and <= CorrelationIdMaxLength } ? correlationId : null, now);
    }

    /// <summary>Builds a record in one expression; the record itself has no public way to change after creation.</summary>
    private sealed record Draft(MembershipAuditAction Action, Guid TenantId, AuditActor Actor, string? CorrelationId, DateTimeOffset Now)
    {
        public Guid? MembershipId { get; init; }

        public Guid? InvitationId { get; init; }

        public string? SubjectId { get; init; }

        public string? PreviousRole { get; init; }

        public string? NewRole { get; init; }

        public string? PreviousStatus { get; init; }

        public string? NewStatus { get; init; }

        public static implicit operator MembershipAuditRecord(Draft draft) => new()
        {
            Id = Guid.CreateVersion7(draft.Now),
            TenantId = draft.TenantId,
            Action = draft.Action,
            ActorKind = draft.Actor.Kind,
            ActorId = draft.Actor.Id,
            MembershipId = draft.MembershipId,
            InvitationId = draft.InvitationId,
            SubjectId = draft.SubjectId,
            PreviousRole = draft.PreviousRole,
            NewRole = draft.NewRole,
            PreviousStatus = draft.PreviousStatus,
            NewStatus = draft.NewStatus,
            OccurredAt = draft.Now,
            CorrelationId = draft.CorrelationId,
        };
    }
}
