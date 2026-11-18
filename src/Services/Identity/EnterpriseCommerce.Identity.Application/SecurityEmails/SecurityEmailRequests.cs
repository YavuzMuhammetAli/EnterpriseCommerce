using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Domain.SecurityEmails;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Identity.Application.SecurityEmails;

/// <summary>
/// Requests a security email inside an authoritative transaction (ADR-018 sections 2 and 3). The account row is
/// locked first, so concurrent requests from any Identity instance see each other's records and the rate limit cannot
/// be bypassed. A suppressed request records nothing, sends nothing and mints nothing.
/// </summary>
internal static class SecurityEmailRequests
{
    /// <returns>The new delivery ID, or <see langword="null"/> when the request was suppressed.</returns>
    public static async Task<Guid?> TryRequestAsync(
        IAuthoritativeSecurityUnit unit,
        string userId,
        SecurityTokenPurpose purpose,
        CancellationToken cancellationToken)
    {
        var now = await unit.LockAccountForSecurityEmailAsync(userId, cancellationToken);
        if (now is null)
        {
            return null;
        }

        var previous = await unit.ReadSecurityEmailRequestTimesAsync(userId, purpose, now.Value - SecurityEmailRateLimit.Window, cancellationToken);
        if (!SecurityEmailRateLimit.Allows(previous, now.Value))
        {
            return null;
        }

        return await unit.CreateSecurityEmailDeliveryAsync(userId, purpose, cancellationToken);
    }
}

/// <summary>Wakes the worker for a committed delivery, or records it as dropped when the queue is full.</summary>
public sealed partial class SecurityEmailScheduler(
    ISecurityEmailQueue queue,
    ISecurityEmailDeliveryStore store,
    ILogger<SecurityEmailScheduler> logger)
{
    public async Task ScheduleAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        if (queue.TryEnqueue(deliveryId))
        {
            return;
        }

        // The transition is recorded, never silently lost. The user can request a new email.
        var dropped = await store.MarkDroppedAsync(deliveryId, "queue-full", cancellationToken);
        LogQueueFull(logger, deliveryId, dropped);
    }

    [LoggerMessage(EventId = 3300, Level = LogLevel.Warning, Message = "Security email queue full: delivery {DeliveryId} dropped ({Recorded})")]
    private static partial void LogQueueFull(ILogger logger, Guid deliveryId, bool recorded);
}
