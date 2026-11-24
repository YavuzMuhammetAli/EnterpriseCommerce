using System.Text.Json.Serialization;
using EnterpriseCommerce.Cart.Application.Carts;
using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.Api.Carts;

/// <summary><c>POST /api/v1/cart/items</c> (CART_API_CONTRACT.md section 4). No member names an owner or a price.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record AddItemRequest(Guid? ProductId, Guid? VariantId, int? Quantity);

/// <summary><c>PUT /api/v1/cart/items/{itemId}</c> (section 5).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record SetQuantityRequest(int? Quantity);

internal sealed record MoneyResponse(decimal Amount, string Currency);

internal sealed record OptionResponse(string Key, string Value);

internal sealed record ImageResponse(string Url, string? AltText);

internal sealed record PricingResponse(DateTimeOffset CheckedAt, bool Stale);

internal sealed record CartItemResponse(
    Guid Id,
    Guid ProductId,
    Guid VariantId,
    string Name,
    string Sku,
    IReadOnlyList<OptionResponse> Options,
    ImageResponse? Image,
    int Quantity,
    MoneyResponse UnitPrice,
    MoneyResponse LineTotal,
    string Availability,
    DateTimeOffset PriceCheckedAt,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt);

/// <summary>The cart (section 3). Staleness, totals and expiry are computed per response, never stored or cached.</summary>
internal sealed record CartResponse(
    Guid? Id,
    int Version,
    Guid? MerchantTenantId,
    string? Currency,
    IReadOnlyList<CartItemResponse> Items,
    int LineCount,
    int TotalQuantity,
    MoneyResponse? Subtotal,
    PricingResponse? Pricing,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ExpiresAt)
{
    public static readonly CartResponse Empty = new(null, 0, null, null, [], 0, 0, null, null, null, null, null);

    public static CartResponse From(CartView? view, DateTimeOffset now, TimeSpan staleAfter, TimeSpan retention)
    {
        if (view is null)
        {
            return Empty;
        }

        var items = view.Items.Select(line => new CartItemResponse(
            line.Id,
            line.ProductId,
            line.VariantId,
            line.Name,
            line.Sku,
            [.. line.Options.Select(option => new OptionResponse(option.Key, option.Value))],
            line.ImageUrl is null ? null : new ImageResponse(line.ImageUrl, line.ImageAltText),
            line.Quantity,
            new MoneyResponse(line.UnitPrice, line.Currency),
            new MoneyResponse(line.UnitPrice * line.Quantity, line.Currency),
            AvailabilityName(line.Availability),
            line.PriceCheckedAt,
            line.AddedAt,
            line.UpdatedAt)).ToList();

        var available = view.Items.Where(line => line.Availability == ItemAvailability.Available).ToList();
        var subtotal = available.Count > 0 && view.Currency is { } currency
            ? new MoneyResponse(available.Sum(line => line.UnitPrice * line.Quantity), currency)
            : null;
        PricingResponse? pricing = null;
        if (view.Items.Count > 0)
        {
            var checkedAt = view.Items.Min(line => line.PriceCheckedAt);
            pricing = new PricingResponse(checkedAt, now - checkedAt > staleAfter);
        }

        return new CartResponse(
            view.Id,
            view.Version,
            view.MerchantTenantId,
            view.Currency,
            items,
            items.Count,
            view.Items.Sum(line => line.Quantity),
            subtotal,
            pricing,
            view.CreatedAt,
            view.UpdatedAt,
            view.UpdatedAt + retention);
    }

    /// <summary>The contract's open availability set (section 3).</summary>
    public static string AvailabilityName(ItemAvailability availability) => availability switch
    {
        ItemAvailability.Available => "available",
        ItemAvailability.Unavailable => "unavailable",
        ItemAvailability.CurrencyMismatch => "currencyMismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(availability)),
    };
}

/// <summary>One change of a refresh (section 8).</summary>
internal sealed record ChangeResponse(
    Guid ItemId,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MoneyResponse? PreviousUnitPrice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MoneyResponse? UnitPrice)
{
    public static ChangeResponse From(CartChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new ChangeResponse(
            change.ItemId,
            change.Kind switch
            {
                CartChangeKind.PriceChanged => "priceChanged",
                CartChangeKind.CurrencyChanged => "currencyChanged",
                CartChangeKind.BecameUnavailable => "becameUnavailable",
                CartChangeKind.BecameAvailable => "becameAvailable",
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            },
            change.PreviousUnitPrice is { } previous ? new MoneyResponse(previous.Amount, previous.Currency) : null,
            change.UnitPrice is { } current ? new MoneyResponse(current.Amount, current.Currency) : null);
    }
}

internal sealed record RefreshResponse(CartResponse Cart, IReadOnlyList<ChangeResponse> Changes);
