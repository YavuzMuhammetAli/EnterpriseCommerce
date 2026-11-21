using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EnterpriseCommerce.Gateway.IntegrationTests;

/// <summary>
/// The real Gateway on a Kestrel listener with its committed Development routes. Every cluster points at one recording
/// backend, so each test can see exactly which request targets reached a destination. Requests are written as raw
/// HTTP/1.1 over TCP, so that client-side URI normalization cannot hide a path form from the Gateway.
/// </summary>
public sealed class KestrelGatewayFixture : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _received = new();
    private WebApplication? _backend;
    private WebApplicationFactory<Program>? _gateway;
    private int _gatewayPort;

    /// <summary>The request targets (path and query, as sent by the Gateway) that reached the backend.</summary>
    public IReadOnlyCollection<string> Received => _received;

    public async ValueTask InitializeAsync()
    {
        var backend = WebApplication.CreateSlimBuilder();
        backend.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        backend.Logging.ClearProviders();
        _backend = backend.Build();
        _backend.Run(async context =>
        {
            _received.Enqueue(context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget);
            context.Response.Headers["X-Test-Backend"] = "reached";
            await context.Response.WriteAsync("backend");
        });
        await _backend.StartAsync(TestContext.Current.CancellationToken);
        var backendAddress = _backend.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single();

        _gatewayPort = FreePort();
        _gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var cluster in DevelopmentConfiguration.ClusterNames())
            {
                builder.UseSetting($"ReverseProxy:Clusters:{cluster}:Destinations:primary:Address", backendAddress);
            }
        });
        _gateway.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, _gatewayPort));
        _gateway.StartServer();
    }

    /// <summary>Sends one raw HTTP/1.1 request and returns the status code and whether the backend was reached.</summary>
    public async Task<GatewayResponse> SendAsync(string method, string target, string host)
    {
        var before = _received.Count;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, _gatewayPort, TestContext.Current.CancellationToken);
        await using var stream = tcp.GetStream();
        var request = $"{method} {target} HTTP/1.1\r\nHost: {host}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);

        using var reader = new StreamReader(stream, Encoding.ASCII);
        var response = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var status = int.Parse(response.Split(' ', 3)[1], System.Globalization.CultureInfo.InvariantCulture);
        var forwarded = response.Contains("X-Test-Backend: reached", StringComparison.OrdinalIgnoreCase);
        return new GatewayResponse(status, forwarded, forwarded ? _received.Skip(before).LastOrDefault() : null);
    }

    public async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.DisposeAsync();
        }

        if (_backend is not null)
        {
            await _backend.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <param name="Status">The Gateway's status code.</param>
/// <param name="Forwarded">Whether the response came from the backend.</param>
/// <param name="BackendTarget">The request target the backend received, when forwarded.</param>
public sealed record GatewayResponse(int Status, bool Forwarded, string? BackendTarget);

/// <summary>The committed Development route set of the Gateway (<c>appsettings.Development.json</c>).</summary>
internal static class DevelopmentConfiguration
{
    public static IReadOnlyList<(string Name, string Path, IReadOnlyList<string> Hosts)> Routes()
    {
        using var document = Load();
        return [.. document.RootElement.GetProperty("ReverseProxy").GetProperty("Routes").EnumerateObject().Select(route =>
        {
            var match = route.Value.GetProperty("Match");
            IReadOnlyList<string> hosts = match.TryGetProperty("Hosts", out var list) ? [.. list.EnumerateArray().Select(host => host.GetString()!)] : [];
            return (route.Name, match.GetProperty("Path").GetString()!, hosts);
        })];
    }

    public static IReadOnlyList<string> ClusterNames()
    {
        using var document = Load();
        return [.. document.RootElement.GetProperty("ReverseProxy").GetProperty("Clusters").EnumerateObject().Select(cluster => cluster.Name)];
    }

    private static JsonDocument Load() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Gateway", "EnterpriseCommerce.Gateway", "appsettings.Development.json")));

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnterpriseCommerce.sln")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }
}
