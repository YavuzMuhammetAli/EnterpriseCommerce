using StackExchange.Redis;

namespace EnterpriseCommerce.Bff.Sessions;

/// <summary>The connection to the dedicated session Redis.</summary>
internal interface IRedisConnection
{
    IDatabase Database { get; }

    /// <summary>Returns the servers' <c>maxmemory-policy</c>, read through <c>INFO memory</c> (no CONFIG permission needed).</summary>
    Task<string?> GetEvictionPolicyAsync(CancellationToken cancellationToken);
}

internal sealed class RedisConnection(string connectionString) : IRedisConnection, IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _multiplexer = new(() =>
    {
        var options = ConfigurationOptions.Parse(connectionString);

        // Reconnects in the background instead of failing the host; a request meanwhile fails closed (503).
        options.AbortOnConnectFail = false;
        options.ClientName = "ec-bff";
        return ConnectionMultiplexer.Connect(options);
    });

    public IDatabase Database => _multiplexer.Value.GetDatabase();

    public async Task<string?> GetEvictionPolicyAsync(CancellationToken cancellationToken)
    {
        // INFO runs on the primary that receives the session writes. It is read through a script because the client
        // treats INFO as an admin command, and admin mode (FLUSHALL, CONFIG SET, ...) stays disabled for the BFF.
        var info = (string?)await Database.ScriptEvaluateAsync("return redis.call('INFO', 'memory')").WaitAsync(cancellationToken);
        return info?
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("maxmemory_policy:", StringComparison.Ordinal))?
            ["maxmemory_policy:".Length..];
    }

    public void Dispose()
    {
        if (_multiplexer.IsValueCreated)
        {
            _multiplexer.Value.Dispose();
        }
    }
}
