using System.Diagnostics.CodeAnalysis;

namespace EnterpriseCommerce.Catalog.Domain.Products;

/// <summary>
/// A list price (ADR-024 section 4): an amount greater than 0 and at most 1,000,000,000 with at most two decimal
/// places, in one of the supported currencies. Order takes its own immutable snapshot at checkout.
/// </summary>
public sealed record Money
{
    public const decimal MaxAmount = 1_000_000_000m;
    public const int MaxDecimals = 2;

    /// <summary>The supported ISO 4217 currencies.</summary>
    public static readonly IReadOnlySet<string> SupportedCurrencies = new HashSet<string>(StringComparer.Ordinal) { "EUR", "USD", "GBP", "TRY" };

    private Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public string Currency { get; }

    public static bool TryCreate(decimal? amount, string? currency, [NotNullWhen(true)] out Money? money, out string? amountError, out string? currencyError)
    {
        amountError = amount switch
        {
            null => "The amount is required.",
            <= 0 or > MaxAmount => $"The amount must be greater than 0 and at most {MaxAmount}.",
            { } value when decimal.Round(value, MaxDecimals) != value => $"The amount has at most {MaxDecimals} decimal places.",
            _ => null,
        };
        currencyError = currency is not null && SupportedCurrencies.Contains(currency)
            ? null
            : $"The currency must be one of {string.Join(", ", SupportedCurrencies.Order(StringComparer.Ordinal))}.";

        money = amountError is null && currencyError is null ? new Money(amount!.Value, currency!) : null;
        return money is not null;
    }

    /// <summary>Restores a stored price, which was valid when it was written.</summary>
    public static Money Rehydrate(decimal amount, string currency) => new(amount, currency);
}
