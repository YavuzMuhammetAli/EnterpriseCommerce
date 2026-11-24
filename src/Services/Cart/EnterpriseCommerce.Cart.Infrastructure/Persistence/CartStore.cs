using EnterpriseCommerce.Cart.Application.Carts;
using EnterpriseCommerce.Cart.Application.Common;
using EnterpriseCommerce.Cart.Domain.Carts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Cart.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementation of <see cref="ICartStore"/> (ADR-029 section 8): optimistic concurrency with whole-attempt
/// retries.
/// </summary>
/// <remarks>
/// <para>
/// Each attempt gets a fresh scope and context, loads the customer's cart with its lines, runs the decision and saves
/// in one <c>SaveChanges</c>, which is one transaction. The cart's <c>Version</c> is the concurrency token: an attempt
/// that read an older version fails at save, and so does a second first-cart insert of one customer (the unique
/// subject index). The losing attempt is discarded and the decision runs again on the newer state.
/// </para>
/// <para>
/// No lock is taken beyond the statements of the save, and nothing remote happens inside an attempt: the Catalog read
/// of a command is done before it.
/// </para>
/// </remarks>
internal sealed partial class CartStore(IServiceScopeFactory scopes, TimeProvider time, ILogger<CartStore> logger) : ICartStore
{
    public const int MaxAttempts = 3;

    public async Task<CartWriteResult<T>> ExecuteAsync<T>(
        string customerSubject, Func<ICartWriteSession, CartDecision<T>> decide, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerSubject);
        ArgumentNullException.ThrowIfNull(decide);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RunOnceAsync(customerSubject, decide, cancellationToken);
            }
            catch (Exception exception) when (IsLostRace(exception) && attempt < MaxAttempts)
            {
                CartMetrics.RecordConcurrencyRetry();
                LogRetried(logger, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 25) * attempt), cancellationToken);
            }
            catch (Exception exception) when (IsLostRace(exception))
            {
                LogExhausted(logger, attempt);
                throw new CartConcurrencyException("Concurrent changes to the cart won every attempt.", exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException && SqlServerErrors.IsSqlServerFailure(exception))
            {
                throw new DataStoreUnavailableException("The Cart database could not complete the write.", exception);
            }
        }
    }

    /// <summary>A concurrent change won: an older version at save, a concurrent first cart or line, or a deadlock.</summary>
    private static bool IsLostRace(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || (exception is DbUpdateException && SqlServerErrors.IsUniqueViolation(exception))
        || (exception is not OperationCanceledException && SqlServerErrors.IsDeadlock(exception));

    private async Task<CartWriteResult<T>> RunOnceAsync<T>(
        string customerSubject, Func<ICartWriteSession, CartDecision<T>> decide, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CartDbContext>();

        var cart = await context.Carts
            .Include(c => c.Items)
            .SingleOrDefaultAsync(c => c.CustomerSubject == customerSubject, cancellationToken);
        var session = new CartWriteSession(context, cart, customerSubject, time.GetUtcNow());

        var decision = decide(session);
        if (!decision.Save || session.Cart is null)
        {
            return new CartWriteResult<T>(decision.Result, null);
        }

        await context.SaveChangesAsync(cancellationToken);
        return new CartWriteResult<T>(decision.Result, CartView.From(session.Cart, StampOf(context, session.Cart)));
    }

    /// <summary>The row's <c>rowversion</c> after the save, as 16 hexadecimal digits (ADR-030 section 2).</summary>
    internal static string StampOf(CartDbContext context, ShoppingCart cart) =>
        Stamp(context.Entry(cart).Property<byte[]>(CartDbContext.RowVersion).CurrentValue);

    internal static string Stamp(byte[]? rowVersion) =>
        rowVersion is { Length: 8 } ? Convert.ToHexString(rowVersion) : throw new InvalidOperationException("The cart has no row version.");

    [LoggerMessage(EventId = 5030, Level = LogLevel.Information, Message = "Cart write lost a concurrency race on attempt {Attempt}; retrying on the newer state")]
    private static partial void LogRetried(ILogger logger, int attempt);

    [LoggerMessage(EventId = 5031, Level = LogLevel.Warning, Message = "Cart write lost every concurrency race after {Attempt} attempts; answered 409")]
    private static partial void LogExhausted(ILogger logger, int attempt);

    private sealed class CartWriteSession(CartDbContext context, ShoppingCart? cart, string customerSubject, DateTimeOffset now) : ICartWriteSession
    {
        public ShoppingCart? Cart { get; private set; } = cart;

        public DateTimeOffset Now { get; } = now;

        public ShoppingCart StartCart()
        {
            if (Cart is not null)
            {
                throw new InvalidOperationException("The customer already has a cart.");
            }

            Cart = ShoppingCart.Create(Guid.CreateVersion7(), customerSubject, Now);
            context.Carts.Add(Cart);
            return Cart;
        }
    }
}

/// <summary>The read side on SQL Server.</summary>
internal sealed class CartQueries(CartDbContext context) : ICartQueries
{
    public Task<CartView?> FindAsync(string customerSubject, CancellationToken cancellationToken) =>
        SqlServerErrors.GuardAsync("read cart", async () =>
        {
            // Tracked, so that the row version (a shadow property) is read with the cart; the context is per request.
            var cart = await context.Carts
                .Include(c => c.Items)
                .SingleOrDefaultAsync(c => c.CustomerSubject == customerSubject, cancellationToken);
            return cart is null ? null : CartView.From(cart, CartStore.StampOf(context, cart));
        });
}
