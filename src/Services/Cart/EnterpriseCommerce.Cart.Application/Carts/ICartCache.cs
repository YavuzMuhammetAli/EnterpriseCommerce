namespace EnterpriseCommerce.Cart.Application.Carts;

/// <summary>Why a view is written to the cache (ADR-030 section 3).</summary>
public enum CartCacheWrite
{
    /// <summary>A read missed and loaded the view from SQL Server (cache-aside).</summary>
    AfterRead,

    /// <summary>A command committed the view (write-through). A failed write is kept for repair.</summary>
    AfterCommit,
}

/// <summary>
/// The cart cache (ADR-030): an optimization only, never the source of truth. Implementations never throw for a cache
/// failure: a failed read is a miss, and a failed write is logged and counted. Views are written forward only, in the
/// order of their <see cref="CartView.Stamp"/>, so an older view never replaces a newer one.
/// </summary>
public interface ICartCache
{
    /// <summary>The cached view of the customer's cart, or <c>null</c> on a miss or any cache failure.</summary>
    ValueTask<CartView?> GetAsync(string customerSubject, CancellationToken cancellationToken);

    /// <summary>Writes the view unless the cache already holds this or a newer one.</summary>
    ValueTask StoreAsync(string customerSubject, CartView view, CartCacheWrite write, CancellationToken cancellationToken);
}

/// <summary>No cache: every read is served from SQL Server (<c>Cart:Cache:Enabled=false</c>).</summary>
public sealed class NoCartCache : ICartCache
{
    public ValueTask<CartView?> GetAsync(string customerSubject, CancellationToken cancellationToken) => ValueTask.FromResult<CartView?>(null);

    public ValueTask StoreAsync(string customerSubject, CartView view, CartCacheWrite write, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
