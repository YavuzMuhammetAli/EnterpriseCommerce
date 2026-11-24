using EnterpriseCommerce.Cart.Application.Common;

namespace EnterpriseCommerce.Cart.Application.Carts;

/// <summary>
/// Reads the customer's cart (ADR-030 section 3): from the cache when it holds an unexpired view, otherwise from SQL
/// Server, which then repopulates the cache. An expired cart reads as no cart. Catalog is never called.
/// </summary>
public sealed class CartReader(ICartCache cache, ICartQueries queries, CartLifecycleSettings lifecycle, TimeProvider time)
{
    /// <summary>The customer's current cart, or <c>null</c> when there is none (or it expired).</summary>
    public async Task<CartView?> GetAsync(string customerSubject, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerSubject);
        var now = time.GetUtcNow();

        if (await cache.GetAsync(customerSubject, cancellationToken) is { } cached)
        {
            if (!lifecycle.IsExpired(cached.UpdatedAt, now))
            {
                return cached;
            }

            CartMetrics.RecordCacheRead("expired");
        }

        var stored = await queries.FindAsync(customerSubject, cancellationToken);
        if (stored is null || lifecycle.IsExpired(stored.UpdatedAt, now))
        {
            return null;
        }

        await cache.StoreAsync(customerSubject, stored, CartCacheWrite.AfterRead, cancellationToken);
        return stored;
    }
}
