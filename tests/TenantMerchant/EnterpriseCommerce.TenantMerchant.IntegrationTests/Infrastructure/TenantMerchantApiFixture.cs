using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Serilog.Core;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

/// <summary>
/// The real Tenant &amp; Merchant host on a throwaway SQL Server database, with <see cref="TestTokenIssuer"/> as the
/// token issuer, <see cref="TestTokenIssuer.AdminClient"/> as the only allow-listed provisioning client, and
/// <see cref="StubIdentity"/> behind the Identity verification client (with a throwaway private key file).
/// </summary>
public sealed class TenantMerchantApiFixture : IAsyncLifetime
{
    private readonly string _keyPath = Path.Combine(Path.GetTempPath(), $"ec-tm-test-{Guid.NewGuid():N}.pem");
    private WebApplicationFactory<Program>? _factory;

    public SqlServerTestDatabase Database { get; } = new();

    public TestTokenIssuer Tokens { get; } = new();

    public CapturedLogs Logs { get; } = new();

    public StubIdentity Identity { get; } = new();

    public WebApplicationFactory<Program> Factory => _factory ?? throw new InvalidOperationException("The SQL Server test database is not configured.");

    public void RequireAvailable() => Database.RequireAvailable();

    public async ValueTask InitializeAsync()
    {
        await Database.InitializeAsync();
        using (var rsa = RSA.Create(3072))
        {
            await File.WriteAllTextAsync(_keyPath, rsa.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);
        }

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
            builder.UseSetting("TenantMerchant:IdentityVerification:BaseAddress", StubIdentity.BaseAddress.AbsoluteUri);
            builder.UseSetting("TenantMerchant:IdentityVerification:PrivateKeyPath", _keyPath);
            configure?.Invoke(builder);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILogEventSink>(Logs);

                // Every outbound client of the host (only the Identity verification clients exist) reaches the stub.
                services.ConfigureAll<HttpClientFactoryOptions>(options =>
                    options.HttpMessageHandlerBuilderActions.Add(handler => handler.PrimaryHandler = new NonDisposingHandler(Identity)));
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

    /// <summary>
    /// Adds a membership directly in the database and returns its ID. <see cref="MerchantRole.MerchantOwner"/> seeds a
    /// co-owner, which only provisioning creates in production; the tests need several to exercise the last-owner rule.
    /// </summary>
    public async Task<Guid> SeedMemberAsync(Guid tenantId, string subject, MerchantRole role, MembershipStatus status = MembershipStatus.Active)
    {
        Assert.True(SubjectId.TryCreate(subject, out var subjectId));
        var membership = role == MerchantRole.MerchantOwner
            ? Membership.CreateOwner(tenantId, subjectId, DateTimeOffset.UtcNow)
            : Membership.CreateMember(tenantId, subjectId, role, DateTimeOffset.UtcNow);
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
        return membership.Id;
    }

    /// <summary>The ID of the subject's current membership in the tenant.</summary>
    public async Task<Guid> MembershipIdAsync(Guid tenantId, string subject) =>
        await Database.ScalarAsync<Guid>(
            $"SELECT Id FROM Memberships WHERE TenantId = '{tenantId}' AND SubjectId = N'{subject.Replace("'", "''", StringComparison.Ordinal)}' AND Status <> N'Removed'");

    /// <summary>Sends a JSON request (or none) as the given user.</summary>
    public async Task<HttpResponseMessage> SendAsUserAsync(string subject, HttpMethod method, string path, object? body = null)
    {
        using var client = CreateClient(UserToken(subject));
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : body as HttpContent ?? JsonContent.Create(body) };
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>Invites an address as <paramref name="inviter"/> and returns the created invitation with its token.</summary>
    public async Task<CreatedInvitationDto> InviteAsync(Guid tenantId, string inviter, string email, string role = "MerchantStaff")
    {
        using var response = await SendAsUserAsync(inviter, HttpMethod.Post, $"/api/v1/tenants/{tenantId}/invitations", new { email, role });
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedInvitationDto>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Accepts an invitation as <paramref name="subject"/> (register its account at <see cref="Identity"/> first).</summary>
    public async Task<HttpResponseMessage> AcceptAsync(string subject, string token) =>
        await SendAsUserAsync(subject, HttpMethod.Post, "/api/v1/merchant-invitations/accept", new { token });

    /// <summary>A membership as stored.</summary>
    public async Task<(string Role, string Status)> StoredMembershipAsync(Guid membershipId)
    {
        await using var context = Database.CreateContext();
        var membership = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(context.Memberships), m => m.Id == membershipId, TestContext.Current.CancellationToken);
        return (membership.Role.ToString(), membership.Status.ToString());
    }

    /// <summary>The number of audit records of a tenant, optionally of one action.</summary>
    public Task<int> AuditCountAsync(Guid tenantId, string? action = null) =>
        Database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM MembershipAuditRecords WHERE TenantId = '{tenantId}'" + (action is null ? string.Empty : $" AND Action = N'{action}'"));

    /// <summary>The number of active owners of a tenant.</summary>
    public Task<int> ActiveOwnersAsync(Guid tenantId) =>
        Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE TenantId = '{tenantId}' AND Role = N'MerchantOwner' AND Status = N'Active'");

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        Tokens.Dispose();
        Identity.Dispose();
        File.Delete(_keyPath);
        await Database.DisposeAsync();
    }

    /// <summary>Lets the HTTP client factory rotate handlers without disposing the shared stub.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }
}

public sealed record MerchantProfileDto(string DisplayName);

public sealed record MembershipDto(Guid Id, string SubjectId, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset StatusChangedAt);

public sealed record ProvisionedTenantDto(Guid Id, string Slug, string Status, DateTimeOffset CreatedAt, MerchantProfileDto MerchantProfile, MembershipDto? OwnerMembership);

public sealed record MemberTenantDto(Guid Id, string Slug, string Status, DateTimeOffset CreatedAt, MerchantProfileDto MerchantProfile, MembershipDto Membership);

public sealed record MembershipPageDto(IReadOnlyList<MembershipDto> Items, int Page, int PageSize, int TotalCount);

public sealed record CallerTenantPageDto(IReadOnlyList<MemberTenantDto> Items, int Page, int PageSize, int TotalCount);

public sealed record InvitationDto(
    Guid Id,
    Guid TenantId,
    string Email,
    string Role,
    string Status,
    string InvitedBySubjectId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    string? AcceptedBySubjectId,
    Guid? AcceptedMembershipId,
    DateTimeOffset? CancelledAt,
    string? CancelledBySubjectId,
    DateTimeOffset? ExpiredAt);

public sealed record CreatedInvitationDto(InvitationDto Invitation, string AcceptanceToken);

public sealed record InvitationPageDto(IReadOnlyList<InvitationDto> Items, int Page, int PageSize, int TotalCount);

public sealed record AcceptedInvitationDto(Guid TenantId, MembershipDto? Membership);
