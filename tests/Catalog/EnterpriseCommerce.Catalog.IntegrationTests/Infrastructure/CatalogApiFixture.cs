using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

/// <summary>
/// The real Catalog host with <see cref="TestTokenIssuer"/> as the token issuer and
/// <see cref="TestTokenIssuer.TaxonomyClient"/> as the only allow-listed taxonomy client. Without a derived fixture,
/// the host points at an unreachable MongoDB: it starts and stays live, but is never ready.
/// </summary>
public class CatalogApiFixture : IAsyncLifetime
{
    /// <summary>A MongoDB address nothing listens on, with a short server selection timeout.</summary>
    public const string UnreachableMongo = "mongodb://127.0.0.1:9/?serverSelectionTimeoutMS=300&connectTimeoutMS=300";

    private WebApplicationFactory<Program>? _factory;

    public TestTokenIssuer Tokens { get; } = new();

    public CapturedLogs Logs { get; } = new();

    public WebApplicationFactory<Program> Factory => _factory ??= CreateFactory();

    public virtual ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>A host with the fixture settings; <paramref name="configure"/> may override any of them.</summary>
    public WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Catalog:Authentication:Issuer", TestTokenIssuer.Issuer);
            builder.UseSetting("Catalog:Taxonomy:AllowedClientIds:0", TestTokenIssuer.TaxonomyClient);
            builder.UseSetting("ConnectionStrings:CatalogDatabase", UnreachableMongo);
            builder.UseSetting("Catalog:Database:ServerSelectionTimeout", "00:00:00.300");
            ConfigureHost(builder);
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILogEventSink>(Logs);
                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.BackchannelHttpHandler = Tokens.CreateBackChannel());
                ConfigureServices(services);
            });
        });

    public HttpClient CreateClient(string? accessToken = null) => CreateClient(Factory, accessToken);

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, string? accessToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var client = factory.CreateClient();
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        Tokens.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Further host settings of a derived fixture.</summary>
    protected virtual void ConfigureHost(IWebHostBuilder builder)
    {
    }

    /// <summary>Further test services of a derived fixture.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }
}
