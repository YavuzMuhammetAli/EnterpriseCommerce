using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Serilog.Core;

namespace EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

/// <summary>
/// The real Cart host on its own throwaway SQL Server database, with <see cref="TestTokenIssuer"/> as the token issuer,
/// <see cref="StubCatalog"/> behind the Catalog client and the test-controlled clock of the database fixture. The cache
/// is off unless a test (or a derived fixture) enables it, and the cleanup worker is off.
/// </summary>
public class CartApiFixture : IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A SQL Server address that refuses at once: the host starts, and every database call fails.</summary>
    public const string UnreachableDatabase = "Server=tcp:127.0.0.1,1;Connect Timeout=1;TrustServerCertificate=true";

    private readonly Lazy<WebApplicationFactory<Program>> _factory;

    public CartApiFixture() => _factory = new(() => CreateFactory(), LazyThreadSafetyMode.ExecutionAndPublication);

    public SqlServerTestDatabase Database { get; } = new();

    public TestTokenIssuer Tokens { get; } = new();

    public StubCatalog Catalog { get; } = new();

    public CapturedLogs Logs { get; } = new();

    public MutableTime Time => Database.Time;

    /// <summary>The fixture's host, created once even when the first requests are concurrent.</summary>
    public WebApplicationFactory<Program> Factory => _factory.Value;

    public virtual void RequireAvailable() => Database.RequireAvailable();

    public virtual async ValueTask InitializeAsync() => await Database.InitializeAsync();

    /// <summary>A host with the fixture settings; <paramref name="configure"/> may override any of them.</summary>
    public WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Cart:Authentication:Issuer", TestTokenIssuer.Issuer);
            builder.UseSetting("ConnectionStrings:CartDatabase", Database.ConnectionString.Length > 0 ? Database.ConnectionString : UnreachableDatabase);
            builder.UseSetting("Cart:Database:ApplyMigrationsAtStartup", "false");
            builder.UseSetting("Cart:Catalog:BaseAddress", StubCatalog.Address.AbsoluteUri);
            builder.UseSetting("Cart:Cleanup:Enabled", "false");
            ConfigureCache(builder);
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILogEventSink>(Logs);
                services.AddSingleton<TimeProvider>(Time);
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new NonDisposingHandler(Catalog)));
                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.BackchannelHttpHandler = Tokens.CreateBackChannel());
            });
        });

    /// <summary>The cache settings of the fixture's host: off here; the cache fixture turns it on.</summary>
    protected virtual void ConfigureCache(IWebHostBuilder builder) => builder.UseSetting("Cart:Cache:Enabled", "false");

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

    /// <summary>A storefront customer's token for <paramref name="subject"/>.</summary>
    public string CustomerToken(string subject) => Tokens.CreateToken(TokenSpec.Customer(subject));

    public static string NewCustomer() => $"customer-{Guid.NewGuid():N}";

    /// <summary>Sends a request as <paramref name="subject"/> (a customer) with an optional body and headers.</summary>
    public Task<HttpResponseMessage> SendAsync(string? subject, HttpMethod method, string path, object? body = null, IReadOnlyDictionary<string, string>? headers = null) =>
        SendWithTokenAsync(subject is null ? null : CustomerToken(subject), method, path, body, headers);

    public async Task<HttpResponseMessage> SendWithTokenAsync(string? token, HttpMethod method, string path, object? body = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var client = CreateClient(token);
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : body as HttpContent ?? JsonContent.Create(body) };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Adds a variant as <paramref name="subject"/> and asserts 200 or 201; returns the cart.</summary>
    public async Task<JsonElement> AddAsync(string subject, Guid productId, Guid variantId, int quantity = 1)
    {
        using var response = await SendAsync(subject, HttpMethod.Post, "/api/v1/cart/items", new { productId, variantId, quantity });
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await ReadAsync(response);
    }

    public async Task<JsonElement> GetCartAsync(string subject)
    {
        using var response = await SendAsync(subject, HttpMethod.Get, "/api/v1/cart");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await ReadAsync(response);
    }

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
        return document.RootElement.Clone();
    }

    /// <summary>Asserts a Cart problem response and returns its body.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal($"https://errors.enterprisecommerce.example/cart/{code}", problem.GetProperty("type").GetString());
        Assert.True(problem.TryGetProperty("correlationId", out _), "A problem carries the correlation ID.");
        return problem;
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_factory.IsValueCreated)
        {
            await _factory.Value.DisposeAsync();
        }

        await Database.DisposeAsync();
        Tokens.Dispose();
        Catalog.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Lets the HTTP client factory rotate handlers without disposing the shared stub.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
