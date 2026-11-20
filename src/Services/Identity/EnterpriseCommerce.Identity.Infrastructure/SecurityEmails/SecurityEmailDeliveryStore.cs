using EnterpriseCommerce.Identity.Application.SecurityEmails;
using EnterpriseCommerce.Identity.Domain.Accounts;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using EnterpriseCommerce.Identity.Application.Diagnostics;
using EnterpriseCommerce.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.Identity.Infrastructure.SecurityEmails;

/// <summary>
/// SQL Server implementation of the delivery status transitions. Each transition is one conditional
/// <c>UPDATE ... WHERE [Status] = expected</c>, so of a worker and the reconciler only one can win, and all
/// timestamps come from <c>SYSUTCDATETIME()</c>.
/// </summary>
internal sealed class SecurityEmailDeliveryStore(IdentityServiceDbContext context) : ISecurityEmailDeliveryStore
{
    private static readonly string Queued = nameof(SecurityEmailDeliveryStatus.Queued);
    private static readonly string Dispatching = nameof(SecurityEmailDeliveryStatus.Dispatching);

    public async Task<bool> TryClaimAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync(
            $"UPDATE [SecurityEmailDeliveries] SET [Status] = {Dispatching}, [ClaimedAt] = SYSUTCDATETIME() WHERE [Id] = {deliveryId} AND [Status] = {Queued}",
            cancellationToken) == 1;

    public async Task<ClaimedSecurityEmail?> ReadClaimedAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        var claimed = await (
                from delivery in context.SecurityEmailDeliveries.AsNoTracking()
                join user in context.Users.AsNoTracking() on delivery.UserId equals user.Id
                where delivery.Id == deliveryId && delivery.Status == SecurityEmailDeliveryStatus.Dispatching
                select new { delivery.UserId, delivery.Purpose, user.Email, user.Status, user.EmailConfirmed })
            .SingleOrDefaultAsync(cancellationToken);

        return claimed is null || string.IsNullOrEmpty(claimed.Email)
            ? null
            : new ClaimedSecurityEmail(deliveryId, claimed.UserId, claimed.Purpose, claimed.Email, new AccountSignInState(claimed.Status, claimed.EmailConfirmed));
    }

    public async Task<bool> RecordAttemptAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        await context.Database.ExecuteSqlAsync(
            $"UPDATE [SecurityEmailDeliveries] SET [AttemptCount] = [AttemptCount] + 1 WHERE [Id] = {deliveryId} AND [Status] = {Dispatching}",
            cancellationToken) == 1;

    public async Task<bool> CompleteAsync(
        Guid deliveryId,
        SecurityEmailDeliveryStatus status,
        string? errorCategory,
        CancellationToken cancellationToken)
    {
        if (!SecurityEmailDeliveryTransitions.IsAllowed(SecurityEmailDeliveryStatus.Dispatching, status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Not a final status of a dispatching delivery.");
        }

        var statusValue = status.ToString();
        var completed = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE [SecurityEmailDeliveries]
            SET [Status] = {statusValue}, [CompletedAt] = SYSUTCDATETIME(), [ErrorCategory] = {errorCategory}
            WHERE [Id] = {deliveryId} AND [Status] = {Dispatching}
            """,
            cancellationToken) == 1;

        if (completed)
        {
            var reason = new KeyValuePair<string, object?>("reason", errorCategory);
            switch (status)
            {
                case SecurityEmailDeliveryStatus.Failed:
                    IdentityMetrics.SecurityEmailFailed.Add(1, reason);
                    break;
                case SecurityEmailDeliveryStatus.Indeterminate:
                    IdentityMetrics.SecurityEmailIndeterminate.Add(1, reason);
                    break;
            }
        }

        return completed;
    }

    public async Task<bool> MarkDroppedAsync(Guid deliveryId, string errorCategory, CancellationToken cancellationToken)
    {
        var dropped = nameof(SecurityEmailDeliveryStatus.Dropped);
        var marked = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE [SecurityEmailDeliveries]
            SET [Status] = {dropped}, [CompletedAt] = SYSUTCDATETIME(), [ErrorCategory] = {errorCategory}
            WHERE [Id] = {deliveryId} AND [Status] = {Queued}
            """,
            cancellationToken) == 1;

        if (marked)
        {
            IdentityMetrics.SecurityEmailDropped.Add(1, new KeyValuePair<string, object?>("reason", errorCategory));
        }

        return marked;
    }

    public async Task<SecurityEmailReconciliation> ReconcileAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var staleSeconds = (int)staleAfter.TotalSeconds;
        var dropped = nameof(SecurityEmailDeliveryStatus.Dropped);
        var indeterminate = nameof(SecurityEmailDeliveryStatus.Indeterminate);

        // Status only: no resend, no token. Each statement is conditional on the current status, so a delivery the
        // worker claimed meanwhile is no longer Queued and cannot be dropped.
        var staleQueued = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE [SecurityEmailDeliveries]
            SET [Status] = {dropped}, [CompletedAt] = SYSUTCDATETIME(), [ErrorCategory] = 'stale-queued'
            WHERE [Status] = {Queued} AND [CreatedAt] < DATEADD(second, -{staleSeconds}, SYSUTCDATETIME())
            """,
            cancellationToken);

        var staleDispatching = await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE [SecurityEmailDeliveries]
            SET [Status] = {indeterminate}, [CompletedAt] = SYSUTCDATETIME(), [ErrorCategory] = 'stale-dispatching'
            WHERE [Status] = {Dispatching} AND [ClaimedAt] < DATEADD(second, -{staleSeconds}, SYSUTCDATETIME())
            """,
            cancellationToken);

        if (staleQueued > 0)
        {
            IdentityMetrics.SecurityEmailDropped.Add(staleQueued, new KeyValuePair<string, object?>("reason", "stale-queued"));
        }

        if (staleDispatching > 0)
        {
            IdentityMetrics.SecurityEmailIndeterminate.Add(staleDispatching, new KeyValuePair<string, object?>("reason", "stale-dispatching"));
        }

        return new SecurityEmailReconciliation(staleQueued, staleDispatching);
    }
}
