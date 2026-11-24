namespace EnterpriseCommerce.Cart.Domain.Carts;

/// <summary>The limits of a cart (ADR-029 section 4). Constants, not settings: a cart is a shopping list, not a bulk order.</summary>
public static class CartLimits
{
    public const int MinQuantity = 1;
    public const int MaxQuantityPerLine = 99;
    public const int MaxLines = 50;

    public static bool IsValidQuantity(int quantity) => quantity is >= MinQuantity and <= MaxQuantityPerLine;
}

/// <summary>The outcome of a cart command. A refused command changes nothing.</summary>
public enum CartOutcome
{
    /// <summary>A new line was added.</summary>
    Added,

    /// <summary>The variant was already in the cart: its quantity grew, and its snapshot was refreshed.</summary>
    Incremented,

    /// <summary>The command changed the cart.</summary>
    Changed,

    /// <summary>The command was already in effect; nothing changed (no new version).</summary>
    Unchanged,

    /// <summary>The quantity is outside 1 to 99.</summary>
    InvalidQuantity,

    /// <summary>The product belongs to another merchant than the cart's.</summary>
    MerchantMismatch,

    /// <summary>The price is in another currency than the cart's.</summary>
    CurrencyMismatch,

    /// <summary>The line would exceed 99 units.</summary>
    QuantityLimitExceeded,

    /// <summary>The cart already has 50 lines.</summary>
    ItemLimitExceeded,

    /// <summary>The cart has no line with that ID.</summary>
    ItemNotFound,
}

/// <summary>What a refresh changed on a line (ADR-029 section 6). The set is open for clients.</summary>
public enum CartChangeKind
{
    PriceChanged,
    CurrencyChanged,
    BecameUnavailable,
    BecameAvailable,
}

/// <summary>One change a refresh made: the line, the kind, and for price changes the previous and current price.</summary>
public sealed record CartChange(Guid ItemId, CartChangeKind Kind, Money? PreviousUnitPrice = null, Money? UnitPrice = null);

/// <summary>
/// A storefront customer's cart (ADR-029): the customer's intent, never a stock commitment. One cart per customer, one
/// merchant and one currency per cart, one line per variant, bounded lines and quantities.
/// </summary>
/// <remarks>
/// Every change increments <see cref="Version"/> (the optimistic concurrency token and the public entity tag) and
/// stamps <see cref="UpdatedAt"/>. When the last line goes, the merchant and the currency are reset, so the next item
/// may come from any merchant. A command that is refused changes nothing.
/// </remarks>
public sealed class ShoppingCart
{
    private readonly List<CartItem> _items = [];

    private ShoppingCart(Guid id, string customerSubject, DateTimeOffset now)
    {
        Id = id;
        CustomerSubject = customerSubject;
        CreatedAt = now;
        UpdatedAt = now;
    }

    /// <summary>For the persistence layer only.</summary>
    private ShoppingCart()
    {
        CustomerSubject = string.Empty;
    }

    public Guid Id { get; private set; }

    /// <summary>The owner (ADR-029 section 1). Never changes.</summary>
    public string CustomerSubject { get; private set; }

    /// <summary>The single merchant of every line; <c>null</c> while the cart is empty.</summary>
    public Guid? MerchantTenantId { get; private set; }

    /// <summary>The cart's currency; <c>null</c> while the cart is empty.</summary>
    public string? Currency { get; private set; }

    /// <summary>Incremented by every change; 0 only for a cart that was never saved.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<CartItem> Items => _items;

    public static ShoppingCart Create(Guid id, string customerSubject, DateTimeOffset now)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A cart needs an ID.", nameof(id));
        }

        if (!Carts.CustomerSubject.IsValid(customerSubject))
        {
            throw new ArgumentException("A cart needs a valid owner.", nameof(customerSubject));
        }

        return new ShoppingCart(id, customerSubject, now);
    }

    /// <summary>Whether the cart was unchanged for longer than <paramref name="retention"/> (ADR-029 section 9).</summary>
    public bool IsExpired(DateTimeOffset now, TimeSpan retention) => IsExpired(UpdatedAt, now, retention);

    public static bool IsExpired(DateTimeOffset updatedAt, DateTimeOffset now, TimeSpan retention) => updatedAt + retention <= now;

    /// <summary>
    /// Starts an expired cart over: its lines go, and it begins a new lifetime in the same row. The change is saved only
    /// together with the command that follows it.
    /// </summary>
    public void StartOverAfterExpiry(DateTimeOffset now)
    {
        _items.Clear();
        MerchantTenantId = null;
        Currency = null;
        CreatedAt = now;
    }

    /// <summary>
    /// Adds <paramref name="quantity"/> units of the snapshot's variant. A variant already in the cart adds to its line
    /// and takes the new snapshot; otherwise a line with <paramref name="newItemId"/> is added.
    /// </summary>
    public CartOutcome AddItem(Guid newItemId, ProductSnapshot snapshot, int quantity, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!CartLimits.IsValidQuantity(quantity))
        {
            return CartOutcome.InvalidQuantity;
        }

        if (MerchantTenantId is { } merchant && merchant != snapshot.TenantId)
        {
            return CartOutcome.MerchantMismatch;
        }

        if (Currency is { } currency && !string.Equals(currency, snapshot.UnitPrice.Currency, StringComparison.Ordinal))
        {
            return CartOutcome.CurrencyMismatch;
        }

        if (_items.Find(item => item.VariantId == snapshot.VariantId) is { } existing)
        {
            if (existing.Quantity + quantity > CartLimits.MaxQuantityPerLine)
            {
                return CartOutcome.QuantityLimitExceeded;
            }

            existing.SetQuantity(existing.Quantity + quantity, now);
            existing.ApplySnapshot(snapshot, ItemAvailability.Available, now);
            Touch(now);
            return CartOutcome.Incremented;
        }

        if (_items.Count >= CartLimits.MaxLines)
        {
            return CartOutcome.ItemLimitExceeded;
        }

        if (newItemId == Guid.Empty)
        {
            throw new ArgumentException("A line needs an ID.", nameof(newItemId));
        }

        _items.Add(new CartItem(newItemId, snapshot, quantity, now));
        MerchantTenantId = snapshot.TenantId;
        Currency = snapshot.UnitPrice.Currency;
        Touch(now);
        return CartOutcome.Added;
    }

    /// <summary>Sets a line's quantity. Setting the current quantity changes nothing.</summary>
    public CartOutcome SetQuantity(Guid itemId, int quantity, DateTimeOffset now)
    {
        if (!CartLimits.IsValidQuantity(quantity))
        {
            return CartOutcome.InvalidQuantity;
        }

        if (_items.Find(item => item.Id == itemId) is not { } item)
        {
            return CartOutcome.ItemNotFound;
        }

        if (item.Quantity == quantity)
        {
            return CartOutcome.Unchanged;
        }

        item.SetQuantity(quantity, now);
        Touch(now);
        return CartOutcome.Changed;
    }

    public CartOutcome RemoveItem(Guid itemId, DateTimeOffset now)
    {
        if (_items.Find(item => item.Id == itemId) is not { } item)
        {
            return CartOutcome.ItemNotFound;
        }

        _items.Remove(item);
        Touch(now);
        return CartOutcome.Changed;
    }

    /// <summary>Removes every line. Clearing an empty cart changes nothing.</summary>
    public CartOutcome Clear(DateTimeOffset now)
    {
        if (_items.Count == 0)
        {
            return CartOutcome.Unchanged;
        }

        _items.Clear();
        Touch(now);
        return CartOutcome.Changed;
    }

    /// <summary>
    /// Applies a Catalog refresh (ADR-029 section 6). <paramref name="refreshedProducts"/> are the products that were
    /// read; <paramref name="currentVariants"/> are their published, active variants. A line of a product that was not
    /// read (added after the reads started) is left as it is. No line is removed.
    /// </summary>
    public IReadOnlyList<CartChange> Refresh(
        IReadOnlySet<Guid> refreshedProducts, IReadOnlyDictionary<Guid, ProductSnapshot> currentVariants, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(refreshedProducts);
        ArgumentNullException.ThrowIfNull(currentVariants);

        var lines = _items
            .Where(item => refreshedProducts.Contains(item.ProductId))
            .Select(item => (Item: item, Current: CurrentFor(item, currentVariants)))
            .ToList();
        if (lines.Count == 0)
        {
            return [];
        }

        // The cart's currency follows the lines when they agree, and is kept when they do not.
        var currencies = lines.Where(line => line.Current is not null).Select(line => line.Current!.UnitPrice.Currency)
            .Concat(_items.Where(item => !refreshedProducts.Contains(item.ProductId)).Select(item => item.Currency))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var cartCurrency = currencies.Count == 1 ? currencies[0] : Currency;

        var changes = new List<CartChange>();
        foreach (var (item, current) in lines)
        {
            var wasAvailable = item.Availability == ItemAvailability.Available;
            if (current is null)
            {
                if (item.Availability != ItemAvailability.Unavailable)
                {
                    changes.Add(new CartChange(item.Id, CartChangeKind.BecameUnavailable));
                }

                item.MarkUnavailable(now);
                continue;
            }

            var previous = item.Price;
            var availability = string.Equals(current.UnitPrice.Currency, cartCurrency, StringComparison.Ordinal)
                ? ItemAvailability.Available
                : ItemAvailability.CurrencyMismatch;
            item.ApplySnapshot(current, availability, now);

            if (!wasAvailable && availability == ItemAvailability.Available)
            {
                changes.Add(new CartChange(item.Id, CartChangeKind.BecameAvailable));
            }

            if (!string.Equals(previous.Currency, current.UnitPrice.Currency, StringComparison.Ordinal))
            {
                changes.Add(new CartChange(item.Id, CartChangeKind.CurrencyChanged, previous, current.UnitPrice));
            }
            else if (previous.Amount != current.UnitPrice.Amount)
            {
                changes.Add(new CartChange(item.Id, CartChangeKind.PriceChanged, previous, current.UnitPrice));
            }
        }

        Currency = cartCurrency;
        Touch(now);
        return changes;
    }

    private static ProductSnapshot? CurrentFor(CartItem item, IReadOnlyDictionary<Guid, ProductSnapshot> currentVariants) =>
        currentVariants.TryGetValue(item.VariantId, out var current) && current.ProductId == item.ProductId && current.TenantId == item.TenantId
            ? current
            : null;

    private void Touch(DateTimeOffset now)
    {
        Version++;
        UpdatedAt = now;
        if (_items.Count == 0)
        {
            MerchantTenantId = null;
            Currency = null;
        }
    }
}
