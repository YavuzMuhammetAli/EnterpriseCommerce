using System.Net;
using System.Text.Json;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>Cross-service conventions: correlation IDs, problem details, health, and logs without secrets.</summary>
public sealed class HostConventionsTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    [Fact]
    public async Task ACorrelationId_IsEchoedAndAddedToProblemDetails()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.UserToken("nobody"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenants/{Guid.NewGuid()}");
        request.Headers.Add("X-Correlation-ID", "test-correlation-1");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("test-correlation-1", Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("test-correlation-1", problem.RootElement.GetProperty("correlationId").GetString());
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
        Assert.StartsWith("GET /api/v1/tenants/", problem.RootElement.GetProperty("instance").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("with spaces")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task AMissingOrUnusableCorrelationId_IsReplaced(string? supplied)
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        if (supplied is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", supplied);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var echoed = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
        Assert.True(Guid.TryParse(echoed, out _));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_IsAnonymousAndReportsTheStatusWordOnly(string path)
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnreachableDatabase_Is503WithoutDetails()
    {
        // No SQL Server needed: the host points at a closed port.
        await using var factory = fixture.CreateFactory("Server=tcp:127.0.0.1,1;Database=unreachable;Connect Timeout=2;TrustServerCertificate=true");
        using var client = TenantMerchantApiFixture.CreateClient(factory, fixture.AdminToken());

        using var response = await client.GetAsync($"/internal/v1/tenants/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        using var ready = await TenantMerchantApiFixture.CreateClient(factory, null).GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.Contains("Retry-After"));
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Logs_NeverContainTokens()
    {
        fixture.RequireAvailable();
        await using var verbose = fixture.CreateFactory(fixture.Database.ConnectionString, builder =>
        {
            builder.UseSetting("Serilog:MinimumLevel:Default", "Verbose");
            builder.UseSetting("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Verbose");
        });
        var adminToken = fixture.AdminToken();
        var userToken = fixture.UserToken("logged-owner");
        var rejected = fixture.Tokens.CreateToken(new TokenSpec { Audiences = ["identity-api"] });

        using (var admin = TenantMerchantApiFixture.CreateClient(verbose, adminToken))
        {
            using var created = await TenantMerchantApiFixture.ProvisionAsync(admin, ProvisioningApiTests.Body("logged", owner: "logged-owner"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using (var user = TenantMerchantApiFixture.CreateClient(verbose, userToken))
        {
            using var _ = await user.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        }

        using (var other = TenantMerchantApiFixture.CreateClient(verbose, rejected))
        {
            using var _ = await other.GetAsync($"/api/v1/tenants/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        }

        var text = fixture.Logs.AllText().ToList();
        Assert.NotEmpty(text);
        foreach (var token in new[] { adminToken, userToken, rejected })
        {
            var signature = token.Split('.')[2];
            Assert.DoesNotContain(text, line => line.Contains(signature, StringComparison.Ordinal));
            Assert.DoesNotContain(text, line => line.Contains(token.Split('.')[1], StringComparison.Ordinal));
        }

        Assert.DoesNotContain(text, line => line.Contains("Bearer ey", StringComparison.Ordinal));
        Assert.Contains(fixture.Logs.Events, logEvent => logEvent.Properties.ContainsKey("CorrelationId"));
    }
}
