using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Serilog.Core;
using Serilog.Events;

namespace EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;

/// <summary>A clock the tests move forward, for token expiry without waiting.</summary>
public sealed class MutableTimeProvider : TimeProvider
{
    private long _offsetTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
}

public enum RefreshFault
{
    None,

    /// <summary>The request reaches Identity, which redeems the token, but the response is lost (connection reset).</summary>
    LoseResponse,

    /// <summary>The connection cannot be established: the request provably never left the BFF.</summary>
    RefuseConnection,

    /// <summary>Identity answers 503 (for example a ledger failure): the outcome is unknown.</summary>
    ServiceUnavailable,
}

/// <summary>Observes and disturbs the BFF's back channel to Identity.</summary>
public sealed class IdentityChannelControls
{
    private int _refreshRequests;
    private int _tokenRequests;

    public int RefreshRequests => Volatile.Read(ref _refreshRequests);

    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    public volatile RefreshFault Fault;

    /// <summary>Delays every refresh request before it is sent (to widen concurrency windows).</summary>
    public TimeSpan RefreshDelay { get; set; }

    public ConcurrentQueue<string> TokenRequestBodies { get; } = new();

    internal sealed class Handler(IdentityChannelControls controls) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath.EndsWith("/connect/token", StringComparison.Ordinal) != true)
            {
                return await base.SendAsync(request, cancellationToken);
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Interlocked.Increment(ref controls._tokenRequests);
            controls.TokenRequestBodies.Enqueue(body);
            if (!body.Contains("grant_type=refresh_token", StringComparison.Ordinal))
            {
                return await base.SendAsync(request, cancellationToken);
            }

            Interlocked.Increment(ref controls._refreshRequests);
            if (controls.RefreshDelay > TimeSpan.Zero)
            {
                await Task.Delay(controls.RefreshDelay, cancellationToken);
            }

            switch (controls.Fault)
            {
                case RefreshFault.RefuseConnection:
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused (test).");
                case RefreshFault.ServiceUnavailable:
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                case RefreshFault.LoseResponse:
                    using (await base.SendAsync(request, cancellationToken))
                    {
                        throw new HttpRequestException(HttpRequestError.ResponseEnded, "Connection reset after sending (test).");
                    }

                default:
                    return await base.SendAsync(request, cancellationToken);
            }
        }
    }
}

/// <summary>Collects log events, to prove no secret is ever logged.</summary>
public sealed class CapturingSink : ILogEventSink
{
    public ConcurrentQueue<LogEvent> Events { get; } = new();

    public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);

    public IEnumerable<string> Rendered() => Events.Select(e => "[" + e.Level + "] " +
        e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)
        + " " + string.Join(" ", e.Properties.Select(p => p.Key + "=" + p.Value))
        + " " + e.Exception);
}

/// <summary>
/// A TCP forwarder in front of the real session Redis. Cutting it makes Redis truly unreachable for the BFF (real
/// connection failures and timeouts, no mocks); restoring it lets the client reconnect to the same data.
/// </summary>
public sealed class TcpForwarder : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _targetHost;
    private readonly int _targetPort;
    private readonly ConcurrentBag<TcpClient> _connections = [];
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _open = true;

    public TcpForwarder(string targetHost, int targetPort)
    {
        _targetHost = targetHost;
        _targetPort = targetPort;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Cut()
    {
        _open = false;
        foreach (var connection in _connections)
        {
            connection.Dispose();
        }

        _connections.Clear();
    }

    public void Restore() => _open = true;

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (!_open)
            {
                client.Dispose();
                continue;
            }

            _ = PumpAsync(client);
        }
    }

    private async Task PumpAsync(TcpClient client)
    {
        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(_targetHost, _targetPort, _stop.Token);
            _connections.Add(client);
            _connections.Add(upstream);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var a = client.GetStream().CopyToAsync(upstream.GetStream(), linked.Token);
            var b = upstream.GetStream().CopyToAsync(client.GetStream(), linked.Token);
            await Task.WhenAny(a, b);
            await linked.CancelAsync();
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        Cut();
        _stop.Dispose();
    }
}
