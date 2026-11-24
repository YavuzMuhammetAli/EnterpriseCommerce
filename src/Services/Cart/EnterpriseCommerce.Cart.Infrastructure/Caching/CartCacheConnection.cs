using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EnterpriseCommerce.Cart.Infrastructure.Caching;

/// <summary>
/// The connection to the cache Redis (ADR-030 section 4). It is created on first use and reconnects in the background;
/// a command issued while disconnected fails at once instead of waiting in a backlog, so an outage costs no request
/// more than the operation timeout.
/// </summary>
internal sealed class CartCacheConnection : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _multiplexer;

    public CartCacheConnection(IOptions<CartCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value;
        _multiplexer = new Lazy<ConnectionMultiplexer>(() =>
        {
            var configuration = ConfigurationOptions.Parse(settings.ConnectionString!);
            configuration.AbortOnConnectFail = false;
            configuration.BacklogPolicy = BacklogPolicy.FailFast;
            configuration.ConnectTimeout = (int)settings.ConnectTimeout.TotalMilliseconds;
            configuration.AsyncTimeout = (int)settings.OperationTimeout.TotalMilliseconds;
            configuration.SyncTimeout = (int)settings.OperationTimeout.TotalMilliseconds;
            configuration.ClientName = "ec-cart";
            return ConnectionMultiplexer.Connect(configuration);
        });
    }

    public IDatabase Database => _multiplexer.Value.GetDatabase();

    public void Dispose()
    {
        if (_multiplexer.IsValueCreated)
        {
            _multiplexer.Value.Dispose();
        }
    }
}
