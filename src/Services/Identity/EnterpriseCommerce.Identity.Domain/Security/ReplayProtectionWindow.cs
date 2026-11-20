namespace EnterpriseCommerce.Identity.Domain.Security;

/// <summary>
/// How long the records that reject a replayed token must be kept (Phase 2.3 S6, retention).
/// </summary>
/// <remarks>
/// <para>
/// A refresh-token redemption ledger row and a redeemed, revoked or expired OpenIddict token entry are what make a
/// replayed token fail or trigger the reuse policy (ADR-017 section 5). They may be deleted only once every token they
/// could ever reject has expired. No token Identity issues lives longer than the longest configured lifetime: refresh
/// tokens never outlive the absolute session (ADR-017 section 5), and codes, ID and access tokens are shorter. A token
/// created before <c>now − window</c> is therefore expired everywhere, and so is anything issued from it.
/// </para>
/// <para>
/// The safety margin covers clock differences between Identity and the database and in-flight requests. An optional
/// minimum window only lengthens the result, for example while tokens issued under a longer, since reduced lifetime
/// may still be valid.
/// </para>
/// </remarks>
public static class ReplayProtectionWindow
{
    /// <summary>The smallest accepted safety margin.</summary>
    public static readonly TimeSpan MinimumSafetyMargin = TimeSpan.FromHours(1);

    /// <summary>The largest window, a sanity bound that also keeps the value representable in seconds.</summary>
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(3650);

    /// <param name="tokenLifetimes">Every configured token and session lifetime that can bound a token Identity issues.</param>
    /// <param name="safetyMargin">At least <see cref="MinimumSafetyMargin"/>.</param>
    /// <param name="minimumWindow">An optional operator floor. It never shortens the computed window.</param>
    public static TimeSpan Compute(IReadOnlyCollection<TimeSpan> tokenLifetimes, TimeSpan safetyMargin, TimeSpan? minimumWindow = null)
    {
        ArgumentNullException.ThrowIfNull(tokenLifetimes);

        if (tokenLifetimes.Count == 0 || tokenLifetimes.Any(lifetime => lifetime <= TimeSpan.Zero))
        {
            throw new ArgumentException("At least one token lifetime is required, and every lifetime must be positive.", nameof(tokenLifetimes));
        }

        if (safetyMargin < MinimumSafetyMargin)
        {
            throw new ArgumentOutOfRangeException(nameof(safetyMargin), safetyMargin, $"The safety margin must be at least {MinimumSafetyMargin}.");
        }

        var window = tokenLifetimes.Max() + safetyMargin;
        if (minimumWindow is { } floor && floor > window)
        {
            window = floor;
        }

        if (window > MaximumWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenLifetimes), window, $"The replay protection window must not exceed {MaximumWindow}.");
        }

        return window;
    }
}
