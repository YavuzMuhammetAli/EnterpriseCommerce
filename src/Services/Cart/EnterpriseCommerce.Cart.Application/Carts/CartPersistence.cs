using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.Application.Carts;

/// <summary>The read side: committed carts as views, read from SQL Server (ADR-009, selective CQRS).</summary>
public interface ICartQueries
{
    /// <summary>The customer's stored cart, expired or not, or <c>null</c> when there is none.</summary>
    Task<CartView?> FindAsync(string customerSubject, CancellationToken cancellationToken);
}

/// <summary>
/// The write side (ADR-029 section 8): runs a command's decision against the customer's current cart and saves it in
/// one transaction, under optimistic concurrency.
/// </summary>
/// <remarks>
/// The decision may run more than once: when a concurrent change wins, it runs again on a new copy of the newer cart,
/// at most three times in all. It must therefore be a pure function of the cart (no I/O, no side effects outside the
/// cart). When every attempt loses, <see cref="CartConcurrencyException"/> is thrown and nothing is saved.
/// </remarks>
public interface ICartStore
{
    Task<CartWriteResult<T>> ExecuteAsync<T>(string customerSubject, Func<ICartWriteSession, CartDecision<T>> decide, CancellationToken cancellationToken);
}

/// <summary>One attempt of a write: the customer's current cart, the time of the attempt, and a way to start a cart.</summary>
public interface ICartWriteSession
{
    /// <summary>The customer's stored cart, or <c>null</c> when there is none.</summary>
    ShoppingCart? Cart { get; }

    DateTimeOffset Now { get; }

    /// <summary>Creates the customer's cart, to be saved with the decision. Only when <see cref="Cart"/> is <c>null</c>.</summary>
    ShoppingCart StartCart();
}

/// <summary>A decision: whether to save the cart, and the command's result either way.</summary>
public readonly record struct CartDecision<T>(bool Save, T Result)
{
    public static CartDecision<T> Commit(T result) => new(true, result);

    public static CartDecision<T> Discard(T result) => new(false, result);
}

/// <summary>The command's result, and the committed cart when the decision saved it.</summary>
public sealed record CartWriteResult<T>(T Result, CartView? Committed);

/// <summary>Concurrent changes to the cart won every attempt (<c>409 concurrency-conflict</c>). Nothing was saved.</summary>
public sealed class CartConcurrencyException : Exception
{
    public CartConcurrencyException()
    {
    }

    public CartConcurrencyException(string message)
        : base(message)
    {
    }

    public CartConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
