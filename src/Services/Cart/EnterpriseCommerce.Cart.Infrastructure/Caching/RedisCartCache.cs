using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EnterpriseCommerce.Cart.Application.Carts;
using EnterpriseCommerce.Cart.Application.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EnterpriseCommerce.Cart.Infrastructure.Caching;

/// <summary>
/// The cart cache on Redis (ADR-030): one hash per customer, <c>{ rv, p }</c>, written forward only by a
/// compare-and-set script on the cart's <c>rowversion</c>, with a TTL on every write.
/// </summary>
/// <remarks>
/// <para>
/// Redis is never on the correctness path. Every failure is caught, logged without the customer and counted: a failed
/// read is a miss, and a failed write is dropped, or kept for repair when it followed a commit. After a failure the
/// instance bypasses Redis for <see cref="CartCacheOptions.CircuitBreakDuration"/>, so an outage costs one timeout per
/// period, not one per request.
/// </para>
/// <para>
/// A read of a cart whose committed view waits for repair bypasses the cache, so this instance never serves the stale
/// entry it knows about.
/// </para>
/// </remarks>
internal sealed partial class RedisCartCache(
    CartCacheConnection connection,
    CartCacheCircuit circuit,
    CartCacheRepairQueue repairs,
    IOptions<CartCacheOptions> options,
    ILogger<RedisCartCache> logger) : ICartCache
{
    /// <summary>The value's own schema; an entry of another schema is a miss.</summary>
    public const int Schema = 1;

    /// <summary>
    /// Writes <c>ARGV[2]</c> with version <c>ARGV[1]</c> unless the key holds that or a newer version. Versions are 16
    /// upper-case hexadecimal digits, so string order is numeric order. Returns 1 when written, 0 when superseded.
    /// </summary>
    internal const string CompareAndSetScript = """
        local current = redis.call('HGET', KEYS[1], 'rv')
        if current and current >= ARGV[1] then return 0 end
        redis.call('HSET', KEYS[1], 'rv', ARGV[1], 'p', ARGV[2])
        redis.call('PEXPIRE', KEYS[1], ARGV[3])
        return 1
        """;

    private static readonly RedisValue VersionField = "rv";
    private static readonly RedisValue PayloadField = "p";

    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async ValueTask<CartView?> GetAsync(string customerSubject, CancellationToken cancellationToken)
    {
        var key = KeyFor(options.Value.KeyPrefix, customerSubject);
        if (circuit.IsOpen || repairs.Contains(key) || !connection.TryGetDatabase(out var database))
        {
            CartMetrics.RecordCacheRead("bypassed");
            return null;
        }

        try
        {
            var fields = await database.HashGetAsync(key, [VersionField, PayloadField]).WaitAsync(cancellationToken);
            if (fields is not [{ HasValue: true } version, { HasValue: true } payload] || Read(payload.ToString()) is not { } view || view.Stamp != version.ToString())
            {
                CartMetrics.RecordCacheRead("miss");
                return null;
            }

            CartMetrics.RecordCacheRead("hit");
            return view;
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            circuit.Trip();
            CartMetrics.RecordCacheRead("error");
            LogReadFailed(logger, exception.GetType().Name);
            return null;
        }
    }

    public async ValueTask StoreAsync(string customerSubject, CartView view, CartCacheWrite write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        var key = KeyFor(options.Value.KeyPrefix, customerSubject);
        if (circuit.IsOpen || !connection.TryGetDatabase(out var database))
        {
            CartMetrics.RecordCacheWrite("bypassed");
            KeepForRepair(key, view, write);
            return;
        }

        try
        {
            var outcome = await WriteAsync(database, key, view).WaitAsync(cancellationToken);
            CartMetrics.RecordCacheWrite(outcome ? "stored" : "superseded");
        }
        catch (Exception exception) when (IsCacheFailure(exception))
        {
            circuit.Trip();
            CartMetrics.RecordCacheWrite("error");
            LogWriteFailed(logger, write.ToString(), exception.GetType().Name);
            KeepForRepair(key, view, write);
        }
    }

    /// <summary>Runs the compare-and-set for one view. True when written, false when the key held this or a newer one.</summary>
    internal async Task<bool> WriteAsync(IDatabase database, RedisKey key, CartView view)
    {
        var result = await database.ScriptEvaluateAsync(
            CompareAndSetScript,
            [key],
            [view.Stamp, JsonSerializer.Serialize(new CachedCart(Schema, view), PayloadJson), (long)options.Value.TimeToLive.TotalMilliseconds]);
        return (long)result == 1;
    }

    /// <summary>The key of a customer's cart: the subject is hashed, so no key discloses it (ADR-030 section 2).</summary>
    internal static RedisKey KeyFor(string prefix, string customerSubject) =>
        $"{prefix}cart:v1:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(customerSubject)))}";

    internal static bool IsCacheFailure(Exception exception) =>
        exception is RedisException or TimeoutException or ObjectDisposedException
        || (exception is OperationCanceledException && exception.InnerException is TimeoutException);

    private static CartView? Read(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<CachedCart>(payload, PayloadJson) is { Schema: Schema, Cart: { Stamp.Length: 16 } cart } ? cart : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void KeepForRepair(RedisKey key, CartView view, CartCacheWrite write)
    {
        // Only a committed change needs repair: a view read from SQL Server can simply be read again.
        if (write == CartCacheWrite.AfterCommit)
        {
            repairs.Enqueue(key, view);
        }
    }

    [LoggerMessage(EventId = 5300, Level = LogLevel.Warning, Message = "Cart cache read failed ({ExceptionType}); served from SQL Server, cache bypassed for a while")]
    private static partial void LogReadFailed(ILogger logger, string exceptionType);

    [LoggerMessage(EventId = 5301, Level = LogLevel.Warning, Message = "Cart cache write after {Write} failed ({ExceptionType}); cache bypassed for a while")]
    private static partial void LogWriteFailed(ILogger logger, string write, string exceptionType);

    /// <summary>The cached value: the schema number and the view.</summary>
    internal sealed record CachedCart(int Schema, CartView Cart);
}

/// <summary>An instance-local circuit (ADR-030 section 4): after a failure, Redis is bypassed for a while.</summary>
internal sealed class CartCacheCircuit(IOptions<CartCacheOptions> options, TimeProvider time)
{
    private long _openUntilTicks;

    public bool IsOpen => time.GetUtcNow().UtcTicks < Interlocked.Read(ref _openUntilTicks);

    public void Trip() => Interlocked.Exchange(ref _openUntilTicks, (time.GetUtcNow() + options.Value.CircuitBreakDuration).UtcTicks);
}

/// <summary>
/// The committed views whose cache write failed (ADR-030 section 4): one entry per cart key, the newest view, at most
/// <see cref="CartCacheOptions.RepairQueueCapacity"/> entries. In memory and per instance by design: what it loses on a
/// restart is bounded by the TTL.
/// </summary>
internal sealed class CartCacheRepairQueue(IOptions<CartCacheOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public bool Contains(RedisKey key) => !_entries.IsEmpty && _entries.ContainsKey(key.ToString());

    public void Enqueue(RedisKey key, CartView view)
    {
        var name = key.ToString();
        if (!_entries.ContainsKey(name) && _entries.Count >= options.Value.RepairQueueCapacity)
        {
            CartMetrics.RecordCacheRepair("dropped");
            return;
        }

        var entry = new Entry(view, time.GetUtcNow());
        _entries.AddOrUpdate(name, entry, (_, existing) => string.CompareOrdinal(existing.View.Stamp, view.Stamp) >= 0 ? existing : entry);
        CartMetrics.RecordCacheRepair("queued");
    }

    /// <summary>The entries to replay; entries older than the TTL are dropped, since the stale entries they would fix have expired.</summary>
    public IReadOnlyList<KeyValuePair<string, Entry>> Due()
    {
        var oldest = time.GetUtcNow() - options.Value.TimeToLive;
        foreach (var (name, entry) in _entries)
        {
            if (entry.QueuedAt <= oldest && _entries.TryRemove(new KeyValuePair<string, Entry>(name, entry)))
            {
                CartMetrics.RecordCacheRepair("dropped");
            }
        }

        return [.. _entries];
    }

    /// <summary>Removes an entry once replayed, unless a newer view was queued meanwhile.</summary>
    public void Complete(string name, Entry entry)
    {
        if (_entries.TryRemove(new KeyValuePair<string, Entry>(name, entry)))
        {
            CartMetrics.RecordCacheRepair("repaired");
        }
    }

    internal sealed record Entry(CartView View, DateTimeOffset QueuedAt);
}

/// <summary>Replays the repair queue once Redis answers again (ADR-030 section 4). Replaying is safe in any order.</summary>
internal sealed partial class CartCacheRepairWorker(
    RedisCartCache cache,
    CartCacheCircuit circuit,
    CartCacheRepairQueue repairs,
    CartCacheConnection connection,
    IOptions<CartCacheOptions> options,
    TimeProvider time,
    ILogger<CartCacheRepairWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled || options.Value.RepairQueueCapacity == 0)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(options.Value.RepairInterval, time, stoppingToken);
                await RepairOnceAsync();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Replays every due entry until Redis fails again. Returns the number replayed.</summary>
    internal async Task<int> RepairOnceAsync()
    {
        if (repairs.Count == 0 || circuit.IsOpen || !connection.TryGetDatabase(out var database))
        {
            return 0;
        }

        var replayed = 0;
        foreach (var (name, entry) in repairs.Due())
        {
            try
            {
                await cache.WriteAsync(database, name, entry.View);
                repairs.Complete(name, entry);
                replayed++;
            }
            catch (Exception exception) when (RedisCartCache.IsCacheFailure(exception))
            {
                circuit.Trip();
                LogRepairFailed(logger, exception.GetType().Name);
                break;
            }
        }

        return replayed;
    }

    [LoggerMessage(EventId = 5302, Level = LogLevel.Warning, Message = "Cart cache repair failed ({ExceptionType}); retrying after the circuit closes")]
    private static partial void LogRepairFailed(ILogger logger, string exceptionType);
}
