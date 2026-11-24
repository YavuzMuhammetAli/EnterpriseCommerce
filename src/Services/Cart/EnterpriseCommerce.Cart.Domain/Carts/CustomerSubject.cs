namespace EnterpriseCommerce.Cart.Domain.Carts;

/// <summary>
/// The owner of a cart: the Identity subject (<c>sub</c>) of a storefront customer's token (ADR-029 section 1). It is
/// an opaque value, compared ordinally; Cart never takes it from a request.
/// </summary>
public static class CustomerSubject
{
    /// <summary>The longest subject the database stores (<c>Carts.CustomerSubject</c>).</summary>
    public const int MaxLength = 200;

    public static bool IsValid(string? subject) =>
        subject is { Length: > 0 and <= MaxLength } && !subject.Any(char.IsControl) && subject.Trim().Length == subject.Length;
}
