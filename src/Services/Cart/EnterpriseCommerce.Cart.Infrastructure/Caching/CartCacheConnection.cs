using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EnterpriseCommerce.Cart.Infrastructure.Caching;

/// <summary>
/// The connection to the cache Redis (ADR-030 section 4). A request never waits for it to be established: it connects
/// in the background (started at host startup by <see cref="CartCacheWarmup"/>, or by the first use), and until the
/// first connection attempt is over the cache is simply bypassed. Afterwards it reconnects in the background, and a
/// command issued while disconnected fails at once instead of waiting in a backlog (finding F2 of PHASE_6_CART.md).
/// </summary>
internal sealed class CartCacheConnection : IDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> _connecting;

    public CartCacheConnection(IOptions<CartCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        _connecting = new Lazy<Task<ConnectionMultiplexer>>(() =>
        {
            var configuration = ConfigurationOptions.Parse(settings.ConnectionString!);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectRetry = 1;
            configuration.BacklogPolicy = BacklogPolicy.FailFast;
            configuration.ConnectTimeout = (int)settings.ConnectTimeout.TotalMilliseconds;
            configuration.AsyncTimeout = (int)settings.OperationTimeout.TotalMilliseconds;
            configuration.SyncTimeout = (int)settings.OperationTimeout.TotalMilliseconds;
            configuration.ClientName = "ec-cart";
            return Task.Run(() => ConnectionMultiplexer.ConnectAsync(configuration));
        });
    }

    /// <summary>
    /// The database when the first connection attempt is over, whatever its result; otherwise starts it (if needed) and
    /// returns <c>false</c> at once.
    /// </summary>
    public bool TryGetDatabase(out IDatabase database)
    {
        var connecting = _connecting.Value;
        if (connecting.IsCompletedSuccessfully)
        {
            database = connecting.Result.GetDatabase();
            return true;
        }

        database = null!;
        return false;
    }

    /// <summary>Waits for the first connection attempt (health checks and startup only, never a request).</summary>
    public async Task<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken) =>
        (await _connecting.Value.WaitAsync(cancellationToken)).GetDatabase();

    /// <summary>Whether a connection is established.</summary>
    public bool IsConnected => _connecting.IsValueCreated && _connecting.Value.IsCompletedSuccessfully && _connecting.Value.Result.IsConnected;

    public void Dispose()
    {
        if (_connecting.IsValueCreated && _connecting.Value.IsCompletedSuccessfully)
        {
            _connecting.Value.Result.Dispose();
        }
    }
}

/// <summary>
/// Starts the cache connection when the host starts, so the first requests find it ready. Startup never waits for, or
/// fails on, Redis.
/// </summary>
internal sealed partial class CartCacheWarmup(CartCacheConnection connection, IOptions<CartCacheOptions> options, ILogger<CartCacheWarmup> logger)
    : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Enabled)
        {
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await (await connection.GetDatabaseAsync(CancellationToken.None)).PingAsync();
                    }
                    catch (Exception exception) when (exception is RedisException or TimeoutException)
                    {
                        LogWarmupFailed(logger, exception.GetType().Name);
                    }
                },
                CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 5303, Level = LogLevel.Information, Message = "The cart cache is not reachable at startup ({ExceptionType}); requests are served from SQL Server until it is")]
    private static partial void LogWarmupFailed(ILogger logger, string exceptionType);
}
