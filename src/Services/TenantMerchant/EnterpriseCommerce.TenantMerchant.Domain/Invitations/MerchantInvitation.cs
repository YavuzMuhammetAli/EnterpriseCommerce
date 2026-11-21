using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.Domain.Invitations;

/// <summary>
/// The lifecycle of an invitation. <c>Pending</c> is the only state with a way out; <c>Accepted</c>, <c>Cancelled</c>
/// and <c>Expired</c> are terminal. A pending invitation past its expiry is effectively expired even before the
/// transition is recorded (<see cref="MerchantInvitation.EffectiveStatus"/>).
/// </summary>
public enum InvitationStatus
{
    Pending,
    Accepted,
    Cancelled,
    Expired,
}

/// <summary>The outcome of an invitation change. Invalid changes are outcomes, not exceptions.</summary>
public enum InvitationChangeOutcome
{
    Changed,
    Unchanged,

    /// <summary>The invitation is no longer pending (accepted, cancelled or expired), so this change is not possible.</summary>
    InvalidTransition,

    /// <summary>The invitation is pending but past its expiry. The caller records the expiry instead.</summary>
    Expired,
}

/// <summary>
/// An invitation to join one tenant with one non-owner role (ADR-021). It holds the hash of a single-use acceptance
/// token, never the token. Accepting it creates a membership for the authenticated subject whose verified email is the
/// invited address; it never creates an Identity account.
/// </summary>
public sealed class MerchantInvitation
{
    /// <summary>The shortest allowed invitation lifetime.</summary>
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromHours(1);

    /// <summary>The longest allowed invitation lifetime.</summary>
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromDays(30);

    private byte[] _tokenHash = [];

    private MerchantInvitation()
    {
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    /// <summary>The invited address as entered (personal data; never logged).</summary>
    public string Email { get; private set; } = string.Empty;

    /// <summary>The upper-invariant address, unique among the tenant's pending invitations.</summary>
    public string NormalizedEmail { get; private set; } = string.Empty;

    /// <summary>The role the membership gets on acceptance. Never <see cref="MerchantRole.MerchantOwner"/>.</summary>
    public MerchantRole Role { get; private set; }

    public InvitationStatus Status { get; private set; }

    /// <summary>The subject of the member who created the invitation.</summary>
    public string InvitedBySubjectId { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public string? AcceptedBySubjectId { get; private set; }

    public Guid? AcceptedMembershipId { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelledBySubjectId { get; private set; }

    public DateTimeOffset? ExpiredAt { get; private set; }

    /// <summary>The token hash bytes (a copy). Mapped to the database; never returned by the API.</summary>
    public byte[] TokenHash
    {
        get => (byte[])_tokenHash.Clone();
        private set => _tokenHash = value;
    }

    /// <summary>Creates a pending invitation.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="role"/> is <see cref="MerchantRole.MerchantOwner"/>, the tenant is empty, or the lifetime is out
    /// of range.
    /// </exception>
    public static MerchantInvitation Create(
        Guid tenantId,
        InviteeEmail email,
        MerchantRole role,
        InvitationTokenHash tokenHash,
        SubjectId invitedBy,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(tokenHash);
        ArgumentNullException.ThrowIfNull(invitedBy);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("An invitation belongs to a tenant.", nameof(tenantId));
        }

        if (role == MerchantRole.MerchantOwner || !Enum.IsDefined(role))
        {
            throw new ArgumentException("An invitation never grants MerchantOwner, which only tenant provisioning creates.", nameof(role));
        }

        if (lifetime < MinimumLifetime || lifetime > MaximumLifetime)
        {
            throw new ArgumentException($"The invitation lifetime must be between {MinimumLifetime} and {MaximumLifetime}.", nameof(lifetime));
        }

        return new MerchantInvitation
        {
            Id = Guid.CreateVersion7(now),
            TenantId = tenantId,
            Email = email.Value,
            NormalizedEmail = email.Normalized,
            Role = role,
            Status = InvitationStatus.Pending,
            InvitedBySubjectId = invitedBy.Value,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            TokenHash = tokenHash.ToArray(),
        };
    }

    /// <summary>Whether the invitation is past its expiry at <paramref name="now"/> (the boundary instant counts as expired).</summary>
    public bool IsPastExpiry(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>The status a reader sees: a pending invitation past its expiry is <c>Expired</c>.</summary>
    public InvitationStatus EffectiveStatus(DateTimeOffset now) =>
        Status == InvitationStatus.Pending && IsPastExpiry(now) ? InvitationStatus.Expired : Status;

    /// <summary>Whether the invitation still accepts: pending and not past its expiry.</summary>
    public bool IsOpen(DateTimeOffset now) => EffectiveStatus(now) == InvitationStatus.Pending;

    /// <summary>
    /// Records the acceptance by <paramref name="subject"/>, whose new membership is <paramref name="membershipId"/>.
    /// The caller has verified that the subject's verified email is the invited address.
    /// </summary>
    public InvitationChangeOutcome Accept(SubjectId subject, Guid membershipId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (Status != InvitationStatus.Pending)
        {
            return InvitationChangeOutcome.InvalidTransition;
        }

        if (IsPastExpiry(now))
        {
            return InvitationChangeOutcome.Expired;
        }

        Status = InvitationStatus.Accepted;
        AcceptedAt = now;
        AcceptedBySubjectId = subject.Value;
        AcceptedMembershipId = membershipId;
        return InvitationChangeOutcome.Changed;
    }

    /// <summary>Cancels a pending invitation. Cancelling a cancelled invitation changes nothing.</summary>
    public InvitationChangeOutcome Cancel(SubjectId actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (Status == InvitationStatus.Cancelled)
        {
            return InvitationChangeOutcome.Unchanged;
        }

        if (Status != InvitationStatus.Pending)
        {
            return InvitationChangeOutcome.InvalidTransition;
        }

        if (IsPastExpiry(now))
        {
            return InvitationChangeOutcome.Expired;
        }

        Status = InvitationStatus.Cancelled;
        CancelledAt = now;
        CancelledBySubjectId = actor.Value;
        return InvitationChangeOutcome.Changed;
    }

    /// <summary>
    /// Records that a pending invitation expired. Only possible once it is past its expiry, so a clock can never cut
    /// an invitation short.
    /// </summary>
    public InvitationChangeOutcome MarkExpired(DateTimeOffset now)
    {
        if (Status == InvitationStatus.Expired)
        {
            return InvitationChangeOutcome.Unchanged;
        }

        if (Status != InvitationStatus.Pending || !IsPastExpiry(now))
        {
            return InvitationChangeOutcome.InvalidTransition;
        }

        Status = InvitationStatus.Expired;
        ExpiredAt = now;
        return InvitationChangeOutcome.Changed;
    }
}
