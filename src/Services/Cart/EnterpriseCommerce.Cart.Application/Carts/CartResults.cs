using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.Application.Carts;

/// <summary>The outcome of a cart command; the API maps each to its status and problem (CART_API_CONTRACT.md section 9).</summary>
public enum CartStatus
{
    /// <summary>Done, or already in effect.</summary>
    Ok,

    /// <summary>A new line was added (<c>201</c>).</summary>
    Created,

    /// <summary>The request is malformed; see the errors (<c>400</c>).</summary>
    Invalid,

    /// <summary>No such line in the caller's cart (<c>404</c>).</summary>
    ItemNotFound,

    /// <summary>The product belongs to another merchant than the cart's (<c>409</c>).</summary>
    MerchantMismatch,

    /// <summary>The price is in another currency than the cart's (<c>409</c>).</summary>
    CurrencyMismatch,

    /// <summary>The line would exceed 99 units (<c>409</c>).</summary>
    QuantityLimitExceeded,

    /// <summary>The cart already has 50 lines (<c>409</c>).</summary>
    ItemLimitExceeded,

    /// <summary>Catalog has no published product with that ID (<c>422</c>).</summary>
    ProductUnavailable,

    /// <summary>The product has no active variant with that ID (<c>422</c>).</summary>
    VariantUnavailable,

    /// <summary><c>If-Match</c> does not match the cart's current version (<c>412</c>).</summary>
    PreconditionFailed,

    /// <summary>Concurrent changes kept winning after the retries (<c>409 concurrency-conflict</c>).</summary>
    ConcurrencyConflict,
}

/// <summary>
/// A command's outcome. <see cref="Cart"/> is the caller's cart afterwards (<c>null</c>: no cart, which reads as an
/// empty one); the other members are set where the status needs them.
/// </summary>
public sealed record CartCommandResult(
    CartStatus Status,
    CartView? Cart = null,
    Guid? ItemId = null,
    IReadOnlyList<CartChange>? Changes = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    Guid? CartMerchantTenantId = null,
    string? CartCurrency = null)
{
    public bool Succeeded => Status is CartStatus.Ok or CartStatus.Created;
}
