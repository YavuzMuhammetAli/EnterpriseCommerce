using EnterpriseCommerce.Cart.Application.Catalog;
using EnterpriseCommerce.Cart.Application.Common;
using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.Application.Carts;

/// <summary>
/// The cart commands (ADR-029). Each one is the caller's own: the subject comes from the token, never from the request.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Catalog is read before the database work, so no transaction waits on HTTP; its answer is reused when the
/// store retries the decision.</item>
/// <item>A refused command writes nothing. A committed one is written through to the cache after the commit, and the
/// cache can never fail it (ADR-030 section 4).</item>
/// <item><paramref name="ExpectedVersion"/> of a command is the optional <c>If-Match</c>: <c>null</c> when absent,
/// <c>-1</c> when it cannot match any version. An expired cart's version is 0, like no cart.</item>
/// </list>
/// </remarks>
public sealed class CartCommands(
    ICartStore store,
    ICartQueries queries,
    ICartCache cache,
    CartReader reader,
    ICatalogProductReader catalog,
    CartLifecycleSettings lifecycle)
{
    /// <summary>How many products a refresh reads from Catalog at once (ADR-029 section 6).</summary>
    public const int RefreshParallelism = 4;

    public async Task<CartCommandResult> AddItemAsync(string customerSubject, Guid? productId, Guid? variantId, int? quantity, int? expectedVersion, string? correlationId, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (productId is not { } product || product == Guid.Empty)
        {
            errors["productId"] = ["The product ID is required."];
        }

        if (variantId is not { } variant || variant == Guid.Empty)
        {
            errors["variantId"] = ["The variant ID is required."];
        }

        ValidateQuantity(quantity, errors);
        if (errors.Count > 0)
        {
            return Refused("add", new CartCommandResult(CartStatus.Invalid, Errors: errors));
        }

        var current = await catalog.ReadAsync(productId!.Value, correlationId, cancellationToken);
        if (current is null)
        {
            return Refused("add", new CartCommandResult(CartStatus.ProductUnavailable));
        }

        if (!current.ActiveVariants.TryGetValue(variantId!.Value, out var snapshot))
        {
            return Refused("add", new CartCommandResult(CartStatus.VariantUnavailable));
        }

        var newItemId = Guid.CreateVersion7();
        return await WriteAsync("add", customerSubject, expectedVersion, session =>
        {
            var cart = session.Cart ?? session.StartCart();
            var merchant = cart.MerchantTenantId;
            var currency = cart.Currency;
            return cart.AddItem(newItemId, snapshot, quantity!.Value, session.Now) switch
            {
                CartOutcome.Added => Commit(CartStatus.Created, itemId: newItemId),
                CartOutcome.Incremented => Commit(CartStatus.Ok, itemId: cart.Items.Single(item => item.VariantId == snapshot.VariantId).Id),
                CartOutcome.MerchantMismatch => Discard(CartStatus.MerchantMismatch, merchant: merchant),
                CartOutcome.CurrencyMismatch => Discard(CartStatus.CurrencyMismatch, currency: currency),
                CartOutcome.QuantityLimitExceeded => Discard(CartStatus.QuantityLimitExceeded),
                CartOutcome.ItemLimitExceeded => Discard(CartStatus.ItemLimitExceeded),
                var other => throw new InvalidOperationException($"Unexpected outcome {other}."),
            };
        }, cancellationToken);
    }

    public async Task<CartCommandResult> SetQuantityAsync(string customerSubject, Guid itemId, int? quantity, int? expectedVersion, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        ValidateQuantity(quantity, errors);
        if (errors.Count > 0)
        {
            return Refused("set_quantity", new CartCommandResult(CartStatus.Invalid, Errors: errors));
        }

        return await WriteAsync("set_quantity", customerSubject, expectedVersion, session =>
            session.Cart?.SetQuantity(itemId, quantity!.Value, session.Now) switch
            {
                CartOutcome.Changed => Commit(CartStatus.Ok),
                CartOutcome.Unchanged => Discard(CartStatus.Ok),
                null or CartOutcome.ItemNotFound => Discard(CartStatus.ItemNotFound),
                var other => throw new InvalidOperationException($"Unexpected outcome {other}."),
            }, cancellationToken);
    }

    public Task<CartCommandResult> RemoveItemAsync(string customerSubject, Guid itemId, int? expectedVersion, CancellationToken cancellationToken) =>
        WriteAsync("remove", customerSubject, expectedVersion, session =>
            session.Cart?.RemoveItem(itemId, session.Now) switch
            {
                CartOutcome.Changed => Commit(CartStatus.Ok),
                null or CartOutcome.ItemNotFound => Discard(CartStatus.ItemNotFound),
                var other => throw new InvalidOperationException($"Unexpected outcome {other}."),
            }, cancellationToken);

    public Task<CartCommandResult> ClearAsync(string customerSubject, int? expectedVersion, CancellationToken cancellationToken) =>
        WriteAsync("clear", customerSubject, expectedVersion, session =>
            session.Cart?.Clear(session.Now) switch
            {
                CartOutcome.Changed => Commit(CartStatus.Ok),
                null or CartOutcome.Unchanged => Discard(CartStatus.Ok),
                var other => throw new InvalidOperationException($"Unexpected outcome {other}."),
            }, cancellationToken);

    /// <summary>
    /// Re-reads every distinct product of the cart from Catalog, then applies the answers in one write (ADR-029 section 6).
    /// All or nothing: one failed read fails the refresh before anything is written.
    /// </summary>
    public async Task<CartCommandResult> RefreshAsync(string customerSubject, int? expectedVersion, string? correlationId, CancellationToken cancellationToken)
    {
        var stored = await queries.FindAsync(customerSubject, cancellationToken);
        var productIds = stored is null ? [] : stored.Items.Select(item => item.ProductId).Distinct().ToList();

        var answers = new System.Collections.Concurrent.ConcurrentDictionary<Guid, CatalogProduct?>();
        await Parallel.ForEachAsync(
            productIds,
            new ParallelOptions { MaxDegreeOfParallelism = RefreshParallelism, CancellationToken = cancellationToken },
            async (productId, token) => answers[productId] = await catalog.ReadAsync(productId, correlationId, token));

        var refreshed = answers.Keys.ToHashSet();
        var variants = answers.Values.OfType<CatalogProduct>().SelectMany(product => product.ActiveVariants).ToDictionary(pair => pair.Key, pair => pair.Value);

        return await WriteAsync("refresh", customerSubject, expectedVersion, session =>
        {
            if (session.Cart is not { } cart || !cart.Items.Any(item => refreshed.Contains(item.ProductId)))
            {
                return Discard(CartStatus.Ok, changes: []);
            }

            return Commit(CartStatus.Ok, changes: cart.Refresh(refreshed, variants, session.Now));
        }, cancellationToken);
    }

    private async Task<CartCommandResult> WriteAsync(string command, string customerSubject, int? expectedVersion, Func<ICartWriteSession, CartDecision<Decision>> decide, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerSubject);

        CartWriteResult<Decision> written;
        try
        {
            written = await store.ExecuteAsync(customerSubject, session =>
            {
                // An expired cart is no cart: its version reads as 0, and a command starts it over in the same row.
                var cart = session.Cart;
                var expired = cart is not null && cart.IsExpired(session.Now, lifecycle.InactiveCartRetention);
                var visibleVersion = cart is null || expired ? 0 : cart.Version;
                if (expectedVersion is { } expected && expected != visibleVersion)
                {
                    return CartDecision<Decision>.Discard(new Decision(CartStatus.PreconditionFailed));
                }

                if (expired)
                {
                    cart!.StartOverAfterExpiry(session.Now);
                }

                return decide(session);
            }, cancellationToken);
        }
        catch (CartConcurrencyException)
        {
            CartMetrics.RecordCommand(command, "conflict");
            return new CartCommandResult(CartStatus.ConcurrencyConflict);
        }

        var result = written.Result;
        if (written.Committed is { } committed)
        {
            // After the commit: the request's cancellation no longer applies, and a cache failure never fails it.
            await cache.StoreAsync(customerSubject, committed, CartCacheWrite.AfterCommit, CancellationToken.None);
            CartMetrics.RecordCommand(command, "applied");
            return new CartCommandResult(result.Status, committed, result.ItemId, result.Changes);
        }

        CartMetrics.RecordCommand(command, result.Status == CartStatus.Ok ? "unchanged" : "refused");
        var view = result.Status == CartStatus.Ok ? await reader.GetAsync(customerSubject, cancellationToken) : null;
        return new CartCommandResult(result.Status, view, result.ItemId, result.Changes, CartMerchantTenantId: result.Merchant, CartCurrency: result.Currency);
    }

    private static void ValidateQuantity(int? quantity, Dictionary<string, string[]> errors)
    {
        if (quantity is not { } value || !CartLimits.IsValidQuantity(value))
        {
            errors["quantity"] = [$"The quantity is required: a whole number from {CartLimits.MinQuantity} to {CartLimits.MaxQuantityPerLine}."];
        }
    }

    private static CartCommandResult Refused(string command, CartCommandResult result)
    {
        CartMetrics.RecordCommand(command, "refused");
        return result;
    }

    private static CartDecision<Decision> Commit(CartStatus status, Guid? itemId = null, IReadOnlyList<CartChange>? changes = null) =>
        CartDecision<Decision>.Commit(new Decision(status, itemId, changes));

    private static CartDecision<Decision> Discard(CartStatus status, Guid? merchant = null, string? currency = null, IReadOnlyList<CartChange>? changes = null) =>
        CartDecision<Decision>.Discard(new Decision(status, Changes: changes, Merchant: merchant, Currency: currency));

    private sealed record Decision(CartStatus Status, Guid? ItemId = null, IReadOnlyList<CartChange>? Changes = null, Guid? Merchant = null, string? Currency = null);
}
