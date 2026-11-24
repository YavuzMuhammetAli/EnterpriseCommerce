using System.Diagnostics.Metrics;

namespace EnterpriseCommerce.Cart.Application.Common;

/// <summary>
/// The service's meters (<c>EnterpriseCommerce.Cart</c>; CART_OPERATIONS.md section 4). Tags are bounded closed sets:
/// never a customer, cart, item, product, variant, merchant or key. Exported once the observability phase wires an
/// exporter; tests observe them with a <see cref="MeterListener"/>.
/// </summary>
public static class CartMetrics
{
    public const string MeterName = "EnterpriseCommerce.Cart";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Commands = Meter.CreateCounter<long>("cart.commands", unit: "{command}");
    private static readonly Counter<long> CatalogReads = Meter.CreateCounter<long>("cart.catalog_reads", unit: "{read}");
    private static readonly Counter<long> CacheReads = Meter.CreateCounter<long>("cart.cache.reads", unit: "{read}");
    private static readonly Counter<long> CacheWrites = Meter.CreateCounter<long>("cart.cache.writes", unit: "{write}");
    private static readonly Counter<long> CacheRepairs = Meter.CreateCounter<long>("cart.cache.repairs", unit: "{entry}");
    private static readonly Counter<long> ConcurrencyRetries = Meter.CreateCounter<long>("cart.concurrency_retries", unit: "{retry}");
    private static readonly Counter<long> CartsExpired = Meter.CreateCounter<long>("cart.carts_expired", unit: "{cart}");

    /// <summary>
    /// <c>command</c>: <c>add</c>, <c>set_quantity</c>, <c>remove</c>, <c>clear</c> or <c>refresh</c>;
    /// <c>outcome</c>: <c>applied</c>, <c>unchanged</c>, <c>refused</c> or <c>conflict</c>.
    /// </summary>
    public static void RecordCommand(string command, string outcome) =>
        Commands.Add(1, new KeyValuePair<string, object?>("command", command), new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary><c>outcome</c>: <c>found</c>, <c>not_published</c> or <c>failed</c>.</summary>
    public static void RecordCatalogRead(string outcome) => CatalogReads.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary><c>outcome</c>: <c>hit</c>, <c>miss</c>, <c>expired</c>, <c>bypassed</c> or <c>error</c>.</summary>
    public static void RecordCacheRead(string outcome) => CacheReads.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary><c>outcome</c>: <c>stored</c>, <c>superseded</c>, <c>bypassed</c> or <c>error</c>.</summary>
    public static void RecordCacheWrite(string outcome) => CacheWrites.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary><c>outcome</c>: <c>queued</c>, <c>repaired</c> or <c>dropped</c>.</summary>
    public static void RecordCacheRepair(string outcome) => CacheRepairs.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>A write lost an optimistic concurrency race and was retried on the newer state.</summary>
    public static void RecordConcurrencyRetry() => ConcurrencyRetries.Add(1);

    /// <summary>Expired carts deleted by the cleanup.</summary>
    public static void RecordCartsExpired(long count) => CartsExpired.Add(count);
}
