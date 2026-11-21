using System.Net.Http.Headers;
using System.Net.Http.Json;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

/// <summary>
/// The real Tenant &amp; Merchant host on a throwaway SQL Server database, with <see cref="TestTokenIssuer"/> as the
/// token issuer and <see cref="TestTokenIssuer.AdminClient"/> as the only allow-listed provisioning client.
/// </summary>
public sealed class TenantMerchantApiFixture : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;

    public SqlServerTestDatabase Database { get; } = new();

    public TestTokenIssuer Tokens { get; } = new();

    public CapturedLogs Logs { get; } = new();

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("The SQL Server test database is not configured.");

    public void RequireAvailable() => Database.RequireAvailable();

    public async ValueTask InitializeAsync()
    {
        await Database.InitializeAsync();
        if (Database.IsConfigured)
        {
            _factory = CreateFactory(Database.ConnectionString);
        }
    }

    /// <summary>
    /// A host on the given database. <paramref name="allowedClients"/> is the provisioning allow-list (default: the test
    /// admin client only). <paramref name="configure"/> may override any other setting.
    /// </summary>
    public WebApplicationFactory<Program> CreateFactory(
        string connectionString,
        Action<IWebHostBuilder>? configure = null,
        string[]? allowedClients = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:TenantMerchantDatabase", connectionString);
            builder.UseSetting("TenantMerchant:Authentication:Issuer", TestTokenIssuer.Issuer);
            foreach (var (index, clientId) in (allowedClients ?? [TestTokenIssuer.AdminClient]).Index())
            {
                builder.UseSetting($"TenantMerchant:Provisioning:AllowedClientIds:{index}", clientId);
            }

            builder.UseSetting("TenantMerchant:Database:ApplyMigrationsAtStartup", "false");
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILogEventSink>(Logs);
                services.Configure<JwtBearerOptions>(
                    JwtBearerDefaults.AuthenticationScheme,
                    options => options.BackchannelHttpHandler = Tokens.CreateBackChannel());
            });
        });

    public HttpClient CreateClient(string? accessToken = null) => CreateClient(Factory, accessToken);

    public static HttpClient CreateClient(WebApplicationFactory<Program> factory, string? accessToken)
    {
        var client = factory.CreateClient();
        if (accessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client;
    }

    public string AdminToken() => Tokens.CreateToken(new TokenSpec());

    public string UserToken(string subject) => Tokens.CreateToken(TokenSpec.User(subject));

    public static async Task<HttpResponseMessage> ProvisionAsync(HttpClient client, object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/tenants") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("D"));
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Provisions a tenant through the API as the allow-listed client and returns its ID.</summary>
    public async Task<Guid> ProvisionTenantAsync(string slug, string ownerSubject)
    {
        using var client = CreateClient(AdminToken());
        using var response = await ProvisionAsync(client, new { slug, displayName = $"{slug} store", ownerSubjectId = ownerSubject, actorReference = "admin:test" });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<ProvisionedTenantDto>(TestContext.Current.CancellationToken);
        return created!.Id;
    }

    /// <summary>Adds a non-owner membership directly in the database (S1 has no membership-management API).</summary>
    public async Task SeedMemberAsync(Guid tenantId, string subject, MerchantRole role, MembershipStatus status = MembershipStatus.Active)
    {
        Assert.True(SubjectId.TryCreate(subject, out var subjectId));
        var membership = Membership.CreateMember(tenantId, subjectId, role, DateTimeOffset.UtcNow);
        if (status == MembershipStatus.Suspended)
        {
            membership.Suspend(activeOwnerCount: 1, DateTimeOffset.UtcNow);
        }
        else if (status == MembershipStatus.Removed)
        {
            membership.Remove(activeOwnerCount: 1, DateTimeOffset.UtcNow);
        }

        await using var context = Database.CreateContext();
        context.Memberships.Add(membership);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        Tokens.Dispose();
        await Database.DisposeAsync();
    }
}

public sealed record MerchantProfileDto(string DisplayName);

public sealed record MembershipDto(Guid Id, string SubjectId, string Role, string Status, DateTimeOffset CreatedAt);

public sealed record ProvisionedTenantDto(Guid Id, string Slug, string Status, DateTimeOffset CreatedAt, MerchantProfileDto MerchantProfile, MembershipDto? OwnerMembership);

public sealed record MemberTenantDto(Guid Id, string Slug, string Status, DateTimeOffset CreatedAt, MerchantProfileDto MerchantProfile, MembershipDto Membership);

public sealed record MembershipPageDto(IReadOnlyList<MembershipDto> Items, int Page, int PageSize, int TotalCount);
