using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;

namespace EnterpriseCommerce.TenantMerchant.Application.Invitations;

/// <summary>An invitation, as returned by the API. It never contains the token or its hash.</summary>
/// <param name="Status">The stored status. Readers use <see cref="Effective"/>, which reports a lapsed pending invitation as expired.</param>
public sealed record InvitationView(
    Guid Id,
    Guid TenantId,
    string Email,
    MerchantRole Role,
    InvitationStatus Status,
    string InvitedBySubjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    string? AcceptedBySubjectId,
    Guid? AcceptedMembershipId,
    DateTimeOffset? CancelledAt,
    string? CancelledBySubjectId,
    DateTimeOffset? ExpiredAt)
{
    /// <summary>The view with its effective status at <paramref name="now"/>.</summary>
    public InvitationView Effective(DateTimeOffset now) =>
        Status == InvitationStatus.Pending && now >= ExpiresAt ? this with { Status = InvitationStatus.Expired } : this;

    public static InvitationView Of(MerchantInvitation invitation)
    {
        ArgumentNullException.ThrowIfNull(invitation);
        return new InvitationView(
            invitation.Id,
            invitation.TenantId,
            invitation.Email,
            invitation.Role,
            invitation.Status,
            invitation.InvitedBySubjectId,
            invitation.CreatedAt,
            invitation.ExpiresAt,
            invitation.AcceptedAt,
            invitation.AcceptedBySubjectId,
            invitation.AcceptedMembershipId,
            invitation.CancelledAt,
            invitation.CancelledBySubjectId,
            invitation.ExpiredAt);
    }
}

/// <summary>One page of a tenant's invitations.</summary>
public sealed record InvitationPage(IReadOnlyList<InvitationView> Items, int Page, int PageSize, int TotalCount);

/// <summary>Reads of invitations. Tenant-scoped, except the lookup by token hash used by acceptance.</summary>
public interface IInvitationReadStore
{
    Task<InvitationView?> GetAsync(Guid tenantId, Guid invitationId, CancellationToken cancellationToken);

    /// <summary>
    /// The tenant's invitations, newest first, optionally of one <b>effective</b> status at <paramref name="now"/>.
    /// </summary>
    Task<InvitationPage> ListAsync(Guid tenantId, InvitationStatus? status, DateTimeOffset now, int skip, int take, CancellationToken cancellationToken);

    /// <summary>The invitation whose acceptance token has this hash, in any tenant and status.</summary>
    Task<InvitationView?> FindByTokenHashAsync(InvitationTokenHash tokenHash, CancellationToken cancellationToken);
}
