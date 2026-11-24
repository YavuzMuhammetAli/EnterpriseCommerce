using EnterpriseCommerce.Cart.Application.Carts;

namespace EnterpriseCommerce.Cart.Api.Common;

/// <summary>
/// RFC 9457 problem responses of the Cart API (CART_API_CONTRACT.md section 9). The <c>type</c> URIs follow the
/// placeholder scheme of IDENTITY_API_CONTRACT.md section 1.4.
/// </summary>
internal static class Problems
{
    public const string TypeBase = "https://errors.enterprisecommerce.example/cart/";

    /// <summary>Maps a refused command to its problem.</summary>
    public static IResult For(CartCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Status switch
        {
            CartStatus.Invalid => Results.ValidationProblem(
                (result.Errors ?? new Dictionary<string, string[]>()).ToDictionary(), title: "One or more validation errors occurred.", type: TypeBase + "validation-failed"),
            CartStatus.ItemNotFound => Problem(StatusCodes.Status404NotFound, "cart-item-not-found", "Your cart has no such item."),
            CartStatus.MerchantMismatch => Problem(
                StatusCodes.Status409Conflict,
                "merchant-mismatch",
                "Your cart holds products of another merchant. Check out or clear it before adding this product.",
                ("cartMerchantTenantId", result.CartMerchantTenantId)),
            CartStatus.CurrencyMismatch => Problem(
                StatusCodes.Status409Conflict,
                "currency-mismatch",
                "This product is priced in another currency than your cart.",
                ("cartCurrency", result.CartCurrency)),
            CartStatus.QuantityLimitExceeded => Problem(StatusCodes.Status409Conflict, "quantity-limit-exceeded", "A cart line holds at most 99 units."),
            CartStatus.ItemLimitExceeded => Problem(StatusCodes.Status409Conflict, "cart-item-limit-exceeded", "A cart holds at most 50 different items."),
            CartStatus.ProductUnavailable => Problem(StatusCodes.Status422UnprocessableEntity, "product-unavailable", "The product is not available."),
            CartStatus.VariantUnavailable => Problem(StatusCodes.Status422UnprocessableEntity, "variant-unavailable", "The product has no such variant available."),
            CartStatus.PreconditionFailed => Problem(StatusCodes.Status412PreconditionFailed, "precondition-failed", "Your cart has changed. Read it again before changing it."),
            CartStatus.ConcurrencyConflict => Problem(
                StatusCodes.Status409Conflict, "concurrency-conflict", "Concurrent changes were made to your cart. Read it and try again."),
            _ => throw new InvalidOperationException($"No problem is defined for {result.Status}."),
        };
    }

    private static IResult Problem(int status, string code, string title, params (string Name, object? Value)[] extensions) =>
        Results.Problem(
            statusCode: status,
            title: title,
            type: TypeBase + code,
            extensions: extensions.Length == 0 ? null : extensions.ToDictionary(extension => extension.Name, extension => extension.Value, StringComparer.Ordinal));
}

/// <summary>
/// The cart version over HTTP (ADR-029 section 8): every cart response carries <c>ETag: "&lt;version&gt;"</c>, and every
/// mutation takes an optional <c>If-Match</c>. A malformed, weak or wildcard tag cannot match a version.
/// </summary>
internal static class EntityTags
{
    public static string For(int version) => $"\"{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";

    /// <summary><c>null</c> when the header is absent; otherwise the version, or <c>-1</c> when it cannot match any version.</summary>
    public static int? ExpectedVersion(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.IfMatch.Count == 0)
        {
            return null;
        }

        if (request.Headers.IfMatch is not [{ } value])
        {
            return -1;
        }

        var tag = value.Trim();
        return tag.Length > 2 && tag[0] == '"' && tag[^1] == '"'
            && int.TryParse(tag.AsSpan(1, tag.Length - 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version)
                ? version
                : -1;
    }
}
