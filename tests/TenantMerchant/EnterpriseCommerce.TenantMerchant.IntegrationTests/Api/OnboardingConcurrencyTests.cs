using System.Data;
using System.Diagnostics;
using System.Net;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Persistence;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// Self-service onboarding under concurrency, on a real SQL Server: the per-subject application lock keeps the key and
/// the limit decisions of one user serialized, the unique slug settles races between users, and a lock that is never
/// granted fails the request with <c>503</c> without blocking anyone else.
/// </summary>
public sealed class OnboardingConcurrencyTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    private const int Callers = 8;

    [Fact]
    public async Task ParallelRetriesWithOneKey_CreateExactlyOneTenant_AndReplayItToEveryOtherRequest()
    {
        fixture.RequireAvailable();
        var subject = SelfServiceOnboardingTests.NewSubject();
        var key = Guid.NewGuid().ToString("D");

        var results = await RaceAsync(_ => (subject, "raced-retry", key));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(Callers - 1, results.Count(r => r.Status == HttpStatusCode.OK));
        var tenantId = Assert.Single(results.Select(r => r.TenantId).Distinct())!.Value;
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'raced-retry'"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId));
    }

    [Fact]
    public async Task ParallelRequestsOfOneUserWithDifferentKeys_NeverExceedTheLimit()
    {
        fixture.RequireAvailable();
        var subject = SelfServiceOnboardingTests.NewSubject();

        var results = await RaceAsync(index => (subject, $"raced-limit-{index}", Guid.NewGuid().ToString("D")));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(Callers - 1, results.Count(r => r.Status == HttpStatusCode.Conflict && r.Code == "tenant-limit-reached"));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Tenants WHERE SelfServiceSubjectId = N'{subject}'"));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE SubjectId = N'{subject}'"));
    }

    [Fact]
    public async Task ParallelRequestsOfManyUsersForOneSlug_CreateExactlyOneTenant()
    {
        fixture.RequireAvailable();

        var results = await RaceAsync(_ => (SelfServiceOnboardingTests.NewSubject(), "raced-slug", Guid.NewGuid().ToString("D")));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.Created));
        Assert.Equal(Callers - 1, results.Count(r => r.Status == HttpStatusCode.Conflict && r.Code == "tenant-slug-taken"));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'raced-slug'"));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>(
            "SELECT COUNT(*) FROM Memberships m JOIN Tenants t ON t.Id = m.TenantId WHERE t.Slug = 'raced-slug'"));
    }

    [Fact]
    public async Task ParallelRequestsOfManyUsers_DoNotWaitForEachOther()
    {
        fixture.RequireAvailable();

        var results = await RaceAsync(index => (SelfServiceOnboardingTests.NewSubject(), $"raced-users-{index}", Guid.NewGuid().ToString("D")));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Created, r.Status));
    }

    [Fact]
    public async Task AHeldLock_FailsOnlyThatUsersRequestWith503_AndReleasingItLetsTheRetrySucceed()
    {
        fixture.RequireAvailable();
        var blocked = SelfServiceOnboardingTests.NewSubject();
        Assert.True(SubjectId.TryCreate(blocked, out var blockedSubject));
        var key = Guid.NewGuid().ToString("D");
        var ct = TestContext.Current.CancellationToken;

        await using var connection = new SqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using (var holder = (SqlTransaction)await connection.BeginTransactionAsync(ct))
        {
            await using (var command = new SqlCommand("sp_getapplock", connection, holder) { CommandType = CommandType.StoredProcedure })
            {
                command.Parameters.AddWithValue("@Resource", TenantOnboardingGate.LockResource(blockedSubject));
                command.Parameters.AddWithValue("@LockMode", "Exclusive");
                command.Parameters.AddWithValue("@LockOwner", "Transaction");
                var result = command.Parameters.Add("@Result", SqlDbType.Int);
                result.Direction = ParameterDirection.ReturnValue;
                await command.ExecuteNonQueryAsync(ct);
                Assert.Equal(0, (int)result.Value);
            }

            var waiting = Send(blocked, "held-lock", key);

            // Another user is not blocked by the held lock.
            var timer = Stopwatch.StartNew();
            var other = await Send(SelfServiceOnboardingTests.NewSubject(), "held-lock-other", Guid.NewGuid().ToString("D"));
            Assert.Equal(HttpStatusCode.Created, other.Status);
            Assert.True(timer.Elapsed < TimeSpan.FromMilliseconds(TenantOnboardingGate.LockTimeoutMilliseconds), $"Another user waited {timer.Elapsed}.");

            var timedOut = await waiting;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, timedOut.Status);
            Assert.True(timedOut.RetryAfter);
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'held-lock'"));

            await holder.RollbackAsync(ct);
        }

        var retried = await Send(blocked, "held-lock", key);
        Assert.Equal(HttpStatusCode.Created, retried.Status);
    }

    private async Task<Result[]> RaceAsync(Func<int, (string Subject, string Slug, string Key)> request)
    {
        // Tokens and clients are made one after the other: the test token issuer signs with one RSA key, which is not
        // meant for concurrent use. Only the requests race, and they all start at the same moment, so that the
        // service's decisions really overlap.
        var clients = Enumerable.Range(0, Callers)
            .Select(request)
            .Select(entry => (Request: entry, Client: fixture.CreateClient(fixture.UserToken(entry.Subject))))
            .ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = clients.Select(async entry =>
        {
            await start.Task;
            using (entry.Client)
            {
                return await SendAsync(entry.Client, entry.Request.Slug, entry.Request.Key);
            }
        }).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private async Task<Result> Send(string subject, string slug, string key)
    {
        using var client = fixture.CreateClient(fixture.UserToken(subject));
        return await SendAsync(client, slug, key);
    }

    private static async Task<Result> SendAsync(HttpClient client, string slug, string key)
    {
        using var response = await SelfServiceOnboardingTests.CreateAsync(client, slug, key: key);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Guid? tenantId = response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK
            ? System.Text.Json.JsonSerializer.Deserialize<MemberTenantDto>(body, JsonOptions)!.Id
            : null;
        var code = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
            ? System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("type").GetString()!.Split('/')[^1]
            : null;
        return new Result(response.StatusCode, tenantId, code, response.Headers.Contains("Retry-After"));
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private sealed record Result(HttpStatusCode Status, Guid? TenantId, string? Code, bool RetryAfter);
}
