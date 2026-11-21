using EnterpriseCommerce.TenantMerchant.Application.Invitations;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;

/// <summary>EF Core implementation of <see cref="IInvitationReadStore"/>. No read returns a token hash.</summary>
internal sealed class InvitationReadStore(TenantMerchantDbContext context) : IInvitationReadStore
{
    public Task<InvitationView?> GetAsync(Guid tenantId, Guid invitationId, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("read invitation", () => Views(context.MerchantInvitations
                .AsNoTracking()
                .Where(i => i.TenantId == tenantId && i.Id == invitationId))
            .SingleOrDefaultAsync(cancellationToken));

    public Task<InvitationPage> ListAsync(
        Guid tenantId,
        InvitationStatus? status,
        DateTimeOffset now,
        int skip,
        int take,
        CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("list invitations", async () =>
        {
            var invitations = context.MerchantInvitations.AsNoTracking().Where(i => i.TenantId == tenantId);

            // The filter is on the effective status: a lapsed pending invitation counts as expired.
            invitations = status switch
            {
                InvitationStatus.Pending => invitations.Where(i => i.Status == InvitationStatus.Pending && i.ExpiresAt > now),
                InvitationStatus.Expired => invitations.Where(i => i.Status == InvitationStatus.Expired
                    || (i.Status == InvitationStatus.Pending && i.ExpiresAt <= now)),
                { } other => invitations.Where(i => i.Status == other),
                null => invitations,
            };

            var total = await invitations.CountAsync(cancellationToken);
            var items = await Views(invitations.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Skip(skip).Take(take))
                .ToListAsync(cancellationToken);
            return new InvitationPage(items, Page: 0, take, total);
        });

    public Task<InvitationView?> FindByTokenHashAsync(InvitationTokenHash tokenHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        var hash = tokenHash.ToArray();
        return SqlServerErrors.GuardAsync("find invitation by token", () => Views(context.MerchantInvitations
                .AsNoTracking()
                .Where(i => i.TokenHash == hash))
            .SingleOrDefaultAsync(cancellationToken));
    }

    private static IQueryable<InvitationView> Views(IQueryable<MerchantInvitation> invitations) =>
        invitations.Select(i => new InvitationView(
            i.Id,
            i.TenantId,
            i.Email,
            i.Role,
            i.Status,
            i.InvitedBySubjectId,
            i.CreatedAt,
            i.ExpiresAt,
            i.AcceptedAt,
            i.AcceptedBySubjectId,
            i.AcceptedMembershipId,
            i.CancelledAt,
            i.CancelledBySubjectId,
            i.ExpiredAt));
}
