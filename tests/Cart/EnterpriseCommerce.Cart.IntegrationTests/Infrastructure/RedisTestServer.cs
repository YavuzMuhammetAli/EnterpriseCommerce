using System.Net;
using System.Net.Sockets;
using EnterpriseCommerce.Cart.Infrastructure.Caching;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

/// <summary>
/// The cache Redis for tests, from <c>EC_TEST_REDIS_CACHE</c>, for example
/// <c>127.0.0.1:6379,password=&lt;REDIS_PASSWORD from deploy/docker/.env&gt;</c> with
/// <c>docker compose --profile redis up -d --wait</c>. Without it the tests are skipped with a reason, or fail with
/// <c>EC_REQUIRE_INFRA_TESTS=true</c>. Redis is never mocked: outages are real connections that are refused or cut.
/// Each instance uses its own key prefix and deletes its keys afterwards.
/// </summary>
public sealed class RedisTestServer : IAsyncDisposable
{
    public const string Variable = "EC_TEST_REDIS_CACHE";

    private readonly Lazy<ConnectionMultiplexer> _admin;

    public RedisTestServer()
    {
        // The test's own connection may run SAVE, to persist a stale entry before a restart (CacheRestartTests).
        _admin = new Lazy<ConnectionMultiplexer>(() =>
        {
            var options = ConfigurationOptions.Parse(ConnectionString());
            options.AllowAdmin = true;
            return ConnectionMultiplexer.Connect(options);
        });
    }

    public static string? Configured => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value : null;

    public static bool IsConfigured => Configured is not null;

    public string KeyPrefix { get; } = $"ectest:{Guid.NewGuid():N}:";

    public IDatabase Database => _admin.Value.GetDatabase();

    public static void RequireAvailable()
    {
        if (!IsConfigured)
        {
            Infra.SkipOrFail($"Redis cache test: set {Variable} to the cache Redis (see RedisTestServer).");
        }
    }

    /// <summary>The configured connection string, optionally through another port (a relay or a closed port).</summary>
    public static string ConnectionString(int? port = null)
    {
        var options = ConfigurationOptions.Parse(Configured!);
        options.AbortOnConnectFail = false;
        if (port is not null)
        {
            options.EndPoints.Clear();
            options.EndPoints.Add(IPAddress.Loopback, port.Value);
        }

        return options.ToString(includePassword: true);
    }

    public static IPEndPoint Endpoint()
    {
        var endpoint = ConfigurationOptions.Parse(Configured!).EndPoints[0];
        return endpoint switch
        {
            IPEndPoint ip => ip,
            DnsEndPoint dns => new IPEndPoint(Dns.GetHostAddresses(dns.Host).First(address => address.AddressFamily == AddressFamily.InterNetwork), dns.Port == 0 ? 6379 : dns.Port),
            _ => throw new InvalidOperationException("Unsupported Redis endpoint."),
        };
    }

    /// <summary>A port on which nothing listens: every connection is refused at once.</summary>
    public static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>The cache under test with its collaborators, on <paramref name="connectionString"/>.</summary>
    internal CacheUnderTest CreateCache(string? connectionString = null, TimeProvider? time = null, Action<CartCacheOptions>? configure = null)
    {
        var settings = new CartCacheOptions
        {
            ConnectionString = connectionString ?? ConnectionString(),
            KeyPrefix = KeyPrefix,
            OperationTimeout = TimeSpan.FromMilliseconds(500),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            CircuitBreakDuration = TimeSpan.FromSeconds(1),
            RepairInterval = TimeSpan.FromMilliseconds(100),
        };
        configure?.Invoke(settings);
        var options = Options.Create(settings);
        var clock = time ?? TimeProvider.System;
        var connection = new CartCacheConnection(options);
        var circuit = new CartCacheCircuit(options, clock);
        var repairs = new CartCacheRepairQueue(options, clock);
        var cache = new RedisCartCache(connection, circuit, repairs, options, NullLogger<RedisCartCache>.Instance);
        var worker = new CartCacheRepairWorker(cache, circuit, repairs, connection, options, clock, NullLogger<CartCacheRepairWorker>.Instance);
        return new CacheUnderTest(cache, circuit, repairs, worker, connection);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_admin.IsValueCreated)
        {
            return;
        }

        var multiplexer = _admin.Value;
        foreach (var endpoint in multiplexer.GetEndPoints())
        {
            var server = multiplexer.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: KeyPrefix + "*"))
            {
                await multiplexer.GetDatabase().KeyDeleteAsync(key);
            }
        }

        await multiplexer.DisposeAsync();
    }
}

/// <summary>A cache with its circuit, repair queue and repair worker, built without a host.</summary>
internal sealed record CacheUnderTest(RedisCartCache Cache, CartCacheCircuit Circuit, CartCacheRepairQueue Repairs, CartCacheRepairWorker Repair, CartCacheConnection Connection) : IDisposable
{
    /// <summary>
    /// Waits until the connection is established: commands on a cold connection fail fast by design (finding F2), and a
    /// test of what Redis holds needs Redis to be reached.
    /// </summary>
    public async Task<CacheUnderTest> ConnectedAsync()
    {
        await Eventually.TrueAsync(
            async () =>
            {
                try
                {
                    await (await Connection.GetDatabaseAsync(TestContext.Current.CancellationToken)).PingAsync();
                    return Connection.IsConnected;
                }
                catch (RedisException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(15),
            "the cache Redis is connected");
        return this;
    }

    public void Dispose() => Connection.Dispose();
}

/// <summary>
/// A TCP relay to the real Redis that a test can cut: open connections are closed and new ones refused until it is
/// restored. It simulates a Redis outage and its recovery without touching the shared server.
/// </summary>
public sealed class TcpRelay : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly IPEndPoint _target;
    private readonly List<TcpClient> _open = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _cut;

    public TcpRelay(IPEndPoint target)
    {
        _target = target;
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Closes every open connection and refuses new ones.</summary>
    public void Cut()
    {
        _cut = true;
        lock (_gate)
        {
            foreach (var client in _open)
            {
                client.Dispose();
            }

            _open.Clear();
        }
    }

    public void Restore() => _cut = false;

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        Cut();
        _listener.Stop();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient inbound;
            try
            {
                inbound = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            if (_cut)
            {
                inbound.Dispose();
                continue;
            }

            _ = RelayAsync(inbound);
        }
    }

    private async Task RelayAsync(TcpClient inbound)
    {
        var outbound = new TcpClient();
        try
        {
            await outbound.ConnectAsync(_target, _stop.Token);
            lock (_gate)
            {
                _open.Add(inbound);
                _open.Add(outbound);
            }

            var up = inbound.GetStream().CopyToAsync(outbound.GetStream(), _stop.Token);
            var down = outbound.GetStream().CopyToAsync(inbound.GetStream(), _stop.Token);
            await Task.WhenAny(up, down);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // A cut or a closed side ends the relay of this connection.
        }
        finally
        {
            inbound.Dispose();
            outbound.Dispose();
        }
    }
}
