using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
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

    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"ec-catalog-test-{Guid.NewGuid():N}.pem");
    private readonly RSA _clientKey = RSA.Create(3072);
    private WebApplicationFactory<Program>? _factory;

    public CatalogApiFixture()
    {
        File.WriteAllText(_keyPath, _clientKey.ExportPkcs8PrivateKeyPem());
        TenantMerchant = new StubTenantMerchant(_clientKey);
    }

    public TestTokenIssuer Tokens { get; } = new();

    /// <summary>Identity's token endpoint and Tenant &amp; Merchant's verification endpoint, behind every outbound client.</summary>
    public StubTenantMerchant TenantMerchant { get; }

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
            builder.UseSetting("Catalog:MembershipVerification:BaseAddress", StubTenantMerchant.BaseAddress.AbsoluteUri);
            builder.UseSetting("Catalog:MembershipVerification:TokenEndpoint", StubTenantMerchant.TokenEndpoint.AbsoluteUri);
            builder.UseSetting("Catalog:MembershipVerification:PrivateKeyPath", _keyPath);
            ConfigureHost(builder);
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILogEventSink>(Logs);

                // Every outbound client of the host (only the membership verification clients exist) reaches the stub.
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new NonDisposingHandler(TenantMerchant)));
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
        TenantMerchant.Dispose();
        _clientKey.Dispose();
        File.Delete(_keyPath);
        GC.SuppressFinalize(this);
    }

    /// <summary>A merchant-portal user token for <paramref name="subject"/>.</summary>
    public string UserToken(string subject) => Tokens.CreateToken(TokenSpec.User(subject));

    /// <summary>A service token of the allow-listed taxonomy client.</summary>
    public string TaxonomyToken() => Tokens.CreateToken(new TokenSpec());

    /// <summary>Further host settings of a derived fixture.</summary>
    protected virtual void ConfigureHost(IWebHostBuilder builder)
    {
    }

    /// <summary>Further test services of a derived fixture.</summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

    /// <summary>Lets the HTTP client factory rotate handlers without disposing the shared stub.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
