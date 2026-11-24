namespace EnterpriseCommerce.Cart.Domain.Carts;

/// <summary>
/// A unit price snapshot: a positive decimal amount with at most two decimals in an ISO 4217-shaped currency code. The
/// bounds mirror Catalog's list prices (ADR-024 section 4), so every Catalog price fits and nothing larger is accepted.
/// Which currencies exist is Catalog's decision; Cart checks only the shape.
/// </summary>
public sealed record Money
{
    public const decimal MaxAmount = 1_000_000_000m;
    public const int MaxDecimals = 2;

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static bool TryCreate(decimal amount, string? currency, out Money? money)
    {
        money = IsValidAmount(amount) && IsValidCurrency(currency) ? new Money(amount, currency!) : null;
        return money is not null;
    }

    public static Money Create(decimal amount, string currency) =>
        TryCreate(amount, currency, out var money) ? money! : throw new ArgumentException($"Invalid price: {amount} {currency}.");

    public static bool IsValidAmount(decimal amount) =>
        amount > 0m && amount <= MaxAmount && decimal.Round(amount, MaxDecimals) == amount;

    /// <summary>Three upper-case ASCII letters, like every ISO 4217 code.</summary>
    public static bool IsValidCurrency(string? currency) =>
        currency is { Length: 3 } && currency.All(c => c is >= 'A' and <= 'Z');
}
