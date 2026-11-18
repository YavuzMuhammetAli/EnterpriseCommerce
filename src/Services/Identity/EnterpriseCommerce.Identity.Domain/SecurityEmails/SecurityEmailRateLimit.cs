namespace EnterpriseCommerce.Identity.Domain.SecurityEmails;

/// <summary>
/// Limits on security emails per account and purpose (ADR-018 section 2): at most 3 per hour, at most 10 per day,
/// and at least 60 seconds between two. A suppressed request sends nothing and mints no token; the response the
/// user sees is the same either way.
/// </summary>
public static class SecurityEmailRateLimit
{
    public const int MaxPerHour = 3;
    public const int MaxPerDay = 10;
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);

    /// <summary>The window the caller must load previous requests for.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);

    /// <param name="previousRequests">Creation times of earlier deliveries within <see cref="Window"/>.</param>
    /// <param name="now">The current time, from the same clock as <paramref name="previousRequests"/>.</param>
    public static bool Allows(IReadOnlyCollection<DateTime> previousRequests, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(previousRequests);

        var lastDay = previousRequests.Where(at => at > now - Window).ToList();
        if (lastDay.Count >= MaxPerDay)
        {
            return false;
        }

        if (lastDay.Count(at => at > now.AddHours(-1)) >= MaxPerHour)
        {
            return false;
        }

        return !lastDay.Any(at => at > now - MinimumInterval);
    }
}
