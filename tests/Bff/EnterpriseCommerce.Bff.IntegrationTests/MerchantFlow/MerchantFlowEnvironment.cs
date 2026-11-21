extern alias GatewayApp;
extern alias TenantMerchantApi;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseCommerce.Bff.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Forwarder;
using GatewayProgram = GatewayApp::Program;
using TenantMerchantProgram = TenantMerchantApi::Program;

namespace EnterpriseCommerce.Bff.IntegrationTests.MerchantFlow;

/// <summary>A service client registered at Identity, authenticating with <c>private_key_jwt</c>.</summary>
public sealed class ServiceClient(string clientId, string scope, string audience) : IDisposable
{
    public string ClientId { get; } = clientId;

    public string Scope { get; } = scope;

    public string Audience { get; } = audience;

    public string KeyId { get; } = Guid.NewGuid().ToString("N");

    public RSA Key { get; } = RSA.Create(3072);

    public string JsonWebKeySet
    {
        get
        {
            var parameters = Key.ExportParameters(includePrivateParameters: false);
            return JsonSerializer.Serialize(new
            {
                keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) } },
            });
        }
    }

    public string CreateAssertion(Uri issuer)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            TokenType = "client-authentication+jwt",
            Issuer = ClientId,
            Audience = issuer.AbsoluteUri,
            Subject = new ClaimsIdentity([new Claim("sub", ClientId), new Claim("jti", Guid.NewGuid().ToString())]),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(Key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        });
    }

    public void Dispose() => Key.Dispose();
}

/// <summary>
/// The merchant membership flow end to end, all real hosts in memory (ADR-021): the merchant BFF on the session Redis,
/// the Gateway with its committed Development routes, Tenant &amp; Merchant on its own throwaway database, and Identity,
/// which issues every token and answers Tenant &amp; Merchant's email verification with a real service token.
/// </summary>
/// <remarks>
/// The browser reaches the BFF only. The BFF forwards <c>/api/**</c> to the Gateway at <c>https://localhost:7000/</c>
/// (the Development address), and the Gateway forwards its Tenant &amp; Merchant routes. Only the transport between
/// hosts is in memory; no handler, policy or route is replaced.
/// </remarks>
public sealed class MerchantFlowEnvironment : BffTestEnvironment
{
    public static readonly Uri GatewayAddress = new("https://localhost:7000/");

    private readonly string _tenantMerchantDatabase = $"ec_bff_tm_test_{Guid.NewGuid():N}";
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"ec-bff-tm-{Guid.NewGuid():N}.pem");
    private WebApplicationFactory<TenantMerchantProgram>? _tenantMerchant;
    private WebApplicationFactory<GatewayProgram>? _gateway;

    /// <summary>Tenant &amp; Merchant's own client for Identity's verification API.</summary>
    public ServiceClient TenantMerchantClient { get; } = new("svc-tenant-merchant", "svc.identity.account-verification", "identity-api");

    /// <summary>Stands in for Admin, the provisioning caller.</summary>
    public ServiceClient ProvisioningClient { get; } = new("svc-test-admin", "svc.tenant-merchant.provisioning", "tenant-merchant-api");

    public WebApplicationFactory<GatewayProgram> Gateway => _gateway ?? throw new InvalidOperationException("The infrastructure is not configured.");

    public BffHost? MerchantBff { get; private set; }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        if (!TestInfrastructure.IsConfigured)
        {
            return;
        }

        await File.WriteAllTextAsync(_keyPath, TenantMerchantClient.Key.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);

        _tenantMerchant = new TenantMerchantFactory(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:TenantMerchantDatabase", TestInfrastructure.SqlServerBuilder(_tenantMerchantDatabase).ConnectionString);
            builder.UseSetting("TenantMerchant:Database:ApplyMigrationsAtStartup", "true");
            builder.UseSetting("TenantMerchant:Authentication:Issuer", Issuer.AbsoluteUri);
            builder.UseSetting("TenantMerchant:Provisioning:AllowedClientIds:0", ProvisioningClient.ClientId);
            builder.UseSetting("TenantMerchant:IdentityVerification:BaseAddress", Issuer.AbsoluteUri);
            builder.UseSetting("TenantMerchant:IdentityVerification:PrivateKeyPath", _keyPath);
            builder.UseSetting("TenantMerchant:IdentityVerification:KeyId", TenantMerchantClient.KeyId);
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
            builder.ConfigureTestServices(services =>
            {
                // Metadata, JWKS, the token endpoint and the verification API: all the real Identity host.
                services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => options.BackchannelHttpHandler = Identity.Server.CreateHandler());
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new KeepAliveHandler(Identity.Server.CreateHandler())));
            });
        });
        var tenantMerchantHandler = _tenantMerchant.Server.CreateHandler();

        _gateway = new GatewayFactory(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Serilog:MinimumLevel:Default", "Warning");
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IForwarderHttpClientFactory>(new FixedForwarderFactory(tenantMerchantHandler)));
        });
        var gatewayHandler = _gateway.Server.CreateHandler();

        MerchantBff = CreateBff(Merchant, options =>
        {
            options.DownstreamApiAddress = GatewayAddress.AbsoluteUri;
            options.DownstreamHandler = gatewayHandler;
        });
    }

    /// <summary>Provisions a tenant through the internal API with a real client-credentials token of the provisioning client.</summary>
    public async Task<Guid> ProvisionTenantAsync(string slug, string ownerSubject)
    {
        var ct = TestContext.Current.CancellationToken;
        using var identity = Identity.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = Issuer });
        using var tokenResponse = await identity.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["scope"] = ProvisioningClient.Scope,
            ["client_id"] = ProvisioningClient.ClientId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = ProvisioningClient.CreateAssertion(Issuer),
        }), ct);
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        using var token = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));

        using var client = _tenantMerchant!.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/tenants")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { slug, displayName = $"{slug} store", ownerSubjectId = ownerSubject, actorReference = "admin:e2e" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.RootElement.GetProperty("access_token").GetString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    protected override void ConfigureIdentity(IWebHostBuilder builder, int nextClientIndex)
    {
        foreach (var client in new[] { TenantMerchantClient, ProvisioningClient })
        {
            var prefix = $"Identity:Clients:{nextClientIndex++}:";
            builder.UseSetting(prefix + "ClientId", client.ClientId);
            builder.UseSetting(prefix + "Kind", "Service");
            builder.UseSetting(prefix + "JsonWebKeySet", client.JsonWebKeySet);
            builder.UseSetting(prefix + "ServiceScopes:0:Scope", client.Scope);
            builder.UseSetting(prefix + "ServiceScopes:0:Audience", client.Audience);
        }

        builder.UseSetting("Identity:AccountVerification:AllowedClientIds:0", TenantMerchantClient.ClientId);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_gateway is not null)
        {
            await _gateway.DisposeAsync();
        }

        if (_tenantMerchant is not null)
        {
            await _tenantMerchant.DisposeAsync();
            await DropDatabaseAsync(_tenantMerchantDatabase);
        }

        await base.DisposeAsync();
        TenantMerchantClient.Dispose();
        ProvisioningClient.Dispose();
        File.Delete(_keyPath);
    }

    private sealed class TenantMerchantFactory(Action<IWebHostBuilder> configure) : WebApplicationFactory<TenantMerchantProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);
    }

    private sealed class GatewayFactory(Action<IWebHostBuilder> configure) : WebApplicationFactory<GatewayProgram>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => configure(builder);
    }

    /// <summary>Sends every proxied request of the Gateway to the in-memory Tenant &amp; Merchant host.</summary>
    private sealed class FixedForwarderFactory(HttpMessageHandler handler) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(handler, disposeHandler: false);
    }

    private sealed class KeepAliveHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}
