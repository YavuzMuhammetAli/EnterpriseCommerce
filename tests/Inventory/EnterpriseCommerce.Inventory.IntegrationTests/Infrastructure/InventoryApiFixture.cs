using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Serilog.Core;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;

/// <summary>
/// The real Inventory host on its own throwaway SQL Server database, with <see cref="TestTokenIssuer"/> as the token
/// issuer, <see cref="StubDependencies"/> behind every outbound client, and <see cref="TestTokenIssuer.ReservationClient"/>
/// and <see cref="TestTokenIssuer.OtherReservationClient"/> as the allow-listed reservation clients. The relay is disabled
/// (no Kafka in Development) and the expiry worker is off unless a test enables it.
/// </summary>
public class InventoryApiFixture : IAsyncLifetime
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"ec-inventory-test-{Guid.NewGuid():N}.pem");
    private readonly RSA _clientKey = RSA.Create(3072);
    private WebApplicationFactory<Program>? _factory;

    public InventoryApiFixture()
    {
        File.WriteAllText(_keyPath, _clientKey.ExportPkcs8PrivateKeyPem());
        Dependencies = new StubDependencies(_clientKey);
    }

    public SqlServerTestDatabase Database { get; } = new();

    public TestTokenIssuer Tokens { get; } = new();

    /// <summary>Identity's token endpoint, Tenant &amp; Merchant and Catalog, behind every outbound client.</summary>
    public StubDependencies Dependencies { get; }

    public CapturedLogs Logs { get; } = new();

    public WebApplicationFactory<Program> Factory => _factory ??= CreateFactory();

    public void RequireAvailable() => Database.RequireAvailable();

    public virtual async ValueTask InitializeAsync() => await Database.InitializeAsync();

    /// <summary>A host with the fixture settings; <paramref name="configure"/> may override any of them.</summary>
    public WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Inventory:Authentication:Issuer", TestTokenIssuer.Issuer);
            builder.UseSetting("ConnectionStrings:InventoryDatabase", Database.IsConfigured ? Database.ConnectionString : "Server=tcp:127.0.0.1,1;Connect Timeout=1;TrustServerCertificate=true");
            builder.UseSetting("Inventory:Database:ApplyMigrationsAtStartup", "false");
            builder.UseSetting("Inventory:ServiceClient:TokenEndpoint", StubDependencies.TokenEndpoint.AbsoluteUri);
            builder.UseSetting("Inventory:ServiceClient:PrivateKeyPath", _keyPath);
            builder.UseSetting("Inventory:MembershipVerification:BaseAddress", StubDependencies.TenantMerchantAddress.AbsoluteUri);
            builder.UseSetting("Inventory:CatalogVerification:BaseAddress", StubDependencies.CatalogAddress.AbsoluteUri);
            builder.UseSetting("Inventory:Reservations:AllowedClientIds:0", TestTokenIssuer.ReservationClient);
            builder.UseSetting("Inventory:Reservations:AllowedClientIds:1", TestTokenIssuer.OtherReservationClient);
            builder.UseSetting("Inventory:ReservationExpiry:Enabled", "false");
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILogEventSink>(Logs);
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new NonDisposingHandler(Dependencies)));
                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.BackchannelHttpHandler = Tokens.CreateBackChannel());
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

    /// <summary>A merchant-portal user token for <paramref name="subject"/>.</summary>
    public string UserToken(string subject) => Tokens.CreateToken(TokenSpec.User(subject));

    /// <summary>A service token of an allow-listed reservation client.</summary>
    public string ServiceToken(string client = TestTokenIssuer.ReservationClient) => Tokens.CreateToken(new TokenSpec { Subject = client, ClientId = client });

    /// <summary>A new tenant whose owner, admin and staff members are known to the stub Tenant &amp; Merchant.</summary>
    public Guid NewTenant(out string owner, out string admin, out string staff)
    {
        var tenantId = Guid.CreateVersion7();
        owner = $"owner-{tenantId:N}";
        admin = $"admin-{tenantId:N}";
        staff = $"staff-{tenantId:N}";
        Dependencies.SetMember(tenantId, owner, "MerchantOwner");
        Dependencies.SetMember(tenantId, admin, "MerchantAdmin");
        Dependencies.SetMember(tenantId, staff, "MerchantStaff");
        return tenantId;
    }

    /// <summary>Sends a request with an optional token and headers.</summary>
    public async Task<HttpResponseMessage> SendAsync(string? token, HttpMethod method, string path, object? body = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var client = CreateClient(token);
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : body as HttpContent ?? JsonContent.Create(body) };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Creates an item of a stub Catalog variant as the tenant's owner (asserting 201) and returns it.</summary>
    public async Task<JsonElement> CreateItemAsync(Guid tenantId, string owner, int onHand, Guid? productId = null, Guid? variantId = null, string? sku = null)
    {
        var product = productId ?? Guid.CreateVersion7();
        var variant = variantId ?? Guid.CreateVersion7();
        Dependencies.SetVariant(tenantId, product, variant, sku: sku ?? $"SKU-{variant:N}"[..20]);
        using var response = await SendAsync(UserToken(owner), HttpMethod.Post, $"/api/v1/inventory/tenants/{tenantId}/items", new { productId = product, variantId = variant, initialOnHand = onHand }, Key());
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await ReadAsync(response);
    }

    public static Dictionary<string, string> Key(string? value = null) => new() { ["Idempotency-Key"] = value ?? Guid.NewGuid().ToString("D") };

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
        return document.RootElement.Clone();
    }

    /// <summary>Asserts an Inventory problem response and returns its body.</summary>
    public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == status, $"Expected {status}, got {response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        Assert.Equal($"https://errors.enterprisecommerce.example/inventory/{code}", problem.GetProperty("type").GetString());
        Assert.True(problem.TryGetProperty("correlationId", out _), "A problem carries the correlation ID.");
        return problem;
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await Database.DisposeAsync();
        Tokens.Dispose();
        Dependencies.Dispose();
        _clientKey.Dispose();
        File.Delete(_keyPath);
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
