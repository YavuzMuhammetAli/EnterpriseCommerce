namespace EnterpriseCommerce.Catalog.Application.Common;

/// <summary>
/// The current time as Catalog stores it: UTC, truncated to milliseconds, the precision of a BSON date. A value read
/// back is therefore exactly the value written.
/// </summary>
public static class CatalogClock
{
    public static DateTimeOffset Now(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var now = timeProvider.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }
}
