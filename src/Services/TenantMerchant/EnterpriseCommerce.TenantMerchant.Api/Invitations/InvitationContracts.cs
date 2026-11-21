using System.Text.Json.Serialization;
using EnterpriseCommerce.TenantMerchant.Api.Tenants;
using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Application.Tenants;

namespace EnterpriseCommerce.TenantMerchant.Api.Invitations;

/// <summary><c>POST /api/v1/tenants/{tenantId}/invitations</c> body. Unknown members are rejected with <c>400</c>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record CreateInvitationRequest(string? Email, string? Role);

/// <summary>
/// <c>POST /api/v1/merchant-invitations/accept</c> body: the token only. Unknown members (a subject, a tenant, an
/// email or a role) are rejected with <c>400</c>, so nothing in the request can choose who joins or where.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AcceptInvitationRequest(string? Token)
{
    /// <summary>Never renders the token.</summary>
    public override string ToString() => nameof(AcceptInvitationRequest);
}

/// <summary>An invitation. It never carries the acceptance token or its hash.</summary>
internal sealed record InvitationResponse(
    Guid Id,
    Guid TenantId,
    string Email,
    string Role,
    string Status,
    string InvitedBySubjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    string? AcceptedBySubjectId,
    Guid? AcceptedMembershipId,
    DateTimeOffset? CancelledAt,
    string? CancelledBySubjectId,
    DateTimeOffset? ExpiredAt);

/// <summary>
/// The response of invitation creation: the invitation and, this once only, its acceptance token. The inviter hands
/// the token to the invitee; it cannot be shown again (ADR-021).
/// </summary>
internal sealed record CreatedInvitationResponse(InvitationResponse Invitation, string AcceptanceToken)
{
    /// <summary>Never renders the token.</summary>
    public override string ToString() => $"{nameof(CreatedInvitationResponse)} {{ Invitation = {Invitation.Id} }}";
}

internal sealed record InvitationPageResponse(IReadOnlyList<InvitationResponse> Items, int Page, int PageSize, int TotalCount);

/// <summary>The response of an acceptance: the tenant joined and the caller's new membership.</summary>
internal sealed record AcceptedInvitationResponse(Guid TenantId, MembershipResponse? Membership);

internal static class InvitationContractMapping
{
    public static InvitationResponse ToResponse(InvitationView invitation) => new(
        invitation.Id,
        invitation.TenantId,
        invitation.Email,
        invitation.Role.ToString(),
        invitation.Status.ToString(),
        invitation.InvitedBySubjectId,
        invitation.CreatedAt,
        invitation.ExpiresAt,
        invitation.AcceptedAt,
        invitation.AcceptedBySubjectId,
        invitation.AcceptedMembershipId,
        invitation.CancelledAt,
        invitation.CancelledBySubjectId,
        invitation.ExpiredAt);

    public static InvitationPageResponse ToResponse(InvitationPage page) =>
        new([.. page.Items.Select(ToResponse)], page.Page, page.PageSize, page.TotalCount);

    public static AcceptedInvitationResponse ToAccepted(Guid tenantId, MembershipView? membership) =>
        new(tenantId, membership is null ? null : TenantContractMapping.ToResponse(membership));
}
