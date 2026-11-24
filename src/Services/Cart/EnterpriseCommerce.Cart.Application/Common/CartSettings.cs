using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.Application.Common;

/// <summary>
/// The cart lifecycle (ADR-029 section 9). Configuration section <c>Cart:Lifecycle</c>, bound and validated at startup
/// by the Infrastructure.
/// </summary>
public sealed class CartLifecycleSettings
{
    public const string SectionName = "Cart:Lifecycle";

    /// <summary>A cart unchanged for this long is expired: it reads as empty and is deleted by the cleanup. 1 to 365 days.</summary>
    public TimeSpan InactiveCartRetention { get; set; } = TimeSpan.FromDays(30);

    public bool IsExpired(DateTimeOffset updatedAt, DateTimeOffset now) => ShoppingCart.IsExpired(updatedAt, now, InactiveCartRetention);

    public IEnumerable<string> Validate()
    {
        if (InactiveCartRetention < TimeSpan.FromDays(1) || InactiveCartRetention > TimeSpan.FromDays(365))
        {
            yield return $"{SectionName}:InactiveCartRetention must be 1 to 365 days.";
        }
    }
}

/// <summary>When a cart's prices are reported as stale (ADR-029 section 6). Configuration section <c>Cart:Pricing</c>.</summary>
public sealed class CartPricingSettings
{
    public const string SectionName = "Cart:Pricing";

    /// <summary>Prices checked longer ago than this are <c>stale</c> in responses. 1 minute to 24 hours.</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(15);

    public IEnumerable<string> Validate()
    {
        if (StaleAfter < TimeSpan.FromMinutes(1) || StaleAfter > TimeSpan.FromHours(24))
        {
            yield return $"{SectionName}:StaleAfter must be 1 minute to 24 hours.";
        }
    }
}
