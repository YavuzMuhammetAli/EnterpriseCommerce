using System.Diagnostics;
using System.Net;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// The last-active-owner guarantee under concurrency, against a real SQL Server (ADR-021): owners who suspend, remove or
/// demote each other at the same moment can never leave a tenant without an active owner, and the per-tenant write
/// lock serializes one tenant's changes without blocking other tenants or readers.
/// </summary>
public sealed class OwnerConcurrencyTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    private const int Rounds = 12;

    [Theory]
    [InlineData("suspend")]
    [InlineData("remove")]
    [InlineData("role")]
    public async Task TwoOwnersActingOnEachOther_AtOnce_LeaveExactlyOneActiveOwner(string action)
    {
        fixture.RequireAvailable();
        for (var round = 0; round < Rounds; round++)
        {
            var tenantId = await fixture.ProvisionTenantAsync($"cc-{action}-{round}", $"cc-{action}-{round}-a");
            var ownerA = await fixture.MembershipIdAsync(tenantId, $"cc-{action}-{round}-a");
            var ownerB = await fixture.SeedMemberAsync(tenantId, $"cc-{action}-{round}-b", MerchantRole.MerchantOwner);

            using var start = new Barrier(2);
            var responses = await Task.WhenAll(
                Task.Run(() => ActAsync($"cc-{action}-{round}-a", tenantId, ownerB, action, start)),
                Task.Run(() => ActAsync($"cc-{action}-{round}-b", tenantId, ownerA, action, start)));

            // Exactly one wins. The other was serialized behind it and re-read its own membership under the tenant lock:
            // suspended or removed, it sees no tenant (404); demoted to admin, it may no longer change roles (403).
            Assert.Single(responses, status => status == HttpStatusCode.OK);
            Assert.Single(responses, status => status == (action == "role" ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound));
            Assert.Equal(1, await fixture.ActiveOwnersAsync(tenantId));
            Assert.Equal(1, await fixture.AuditCountAsync(tenantId) - 1);
        }
    }

    [Fact]
    public async Task ThreeOwnersRemovingEveryOtherOwnerAtOnce_AlwaysLeaveOneActiveOwner()
    {
        fixture.RequireAvailable();
        for (var round = 0; round < Rounds; round++)
        {
            var subjects = new[] { $"cc3-{round}-a", $"cc3-{round}-b", $"cc3-{round}-c" };
            var tenantId = await fixture.ProvisionTenantAsync($"cc3-{round}", subjects[0]);
            var memberships = new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                [subjects[0]] = await fixture.MembershipIdAsync(tenantId, subjects[0]),
                [subjects[1]] = await fixture.SeedMemberAsync(tenantId, subjects[1], MerchantRole.MerchantOwner),
                [subjects[2]] = await fixture.SeedMemberAsync(tenantId, subjects[2], MerchantRole.MerchantOwner),
            };

            var attempts = (from actor in subjects
                            from target in subjects
                            where actor != target
                            select (actor, target)).ToList();
            using var start = new Barrier(attempts.Count);
            var responses = await Task.WhenAll(attempts.Select(attempt =>
                Task.Run(() => ActAsync(attempt.actor, tenantId, memberships[attempt.target], round % 2 == 0 ? "remove" : "role", start))));

            // Losers see no tenant (404), may no longer change roles (403), or find the target already removed (409) or
            // already demoted (200, unchanged). Never a server error, and never a tenant without an owner.
            Assert.All(responses, status => Assert.True(
                status is HttpStatusCode.OK or HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Conflict, status.ToString()));
            Assert.Equal(1, await fixture.ActiveOwnersAsync(tenantId));

            // Exactly two owners lost their ownership, and exactly those two changes are audited.
            Assert.Equal(2, await fixture.AuditCountAsync(tenantId) - 1);
        }
    }

    [Fact]
    public async Task TheTenantLock_SerializesWritersOfOneTenant_WithoutBlockingReadersOrOtherTenants()
    {
        fixture.RequireAvailable();
        var locked = await fixture.ProvisionTenantAsync("cc-lock", "cc-lock-owner");
        var lockedStaff = await fixture.SeedMemberAsync(locked, "cc-lock-staff", MerchantRole.MerchantStaff);
        var other = await fixture.ProvisionTenantAsync("cc-lock-other", "cc-lock-other-owner");
        var otherStaff = await fixture.SeedMemberAsync(other, "cc-lock-other-staff", MerchantRole.MerchantStaff);
        var ct = TestContext.Current.CancellationToken;

        // Another writer holds the tenant's lock, exactly as the write gate takes it.
        await using var connection = new SqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var command = new SqlCommand("SELECT 1 FROM Tenants WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = @id", connection, transaction))
        {
            command.Parameters.AddWithValue("@id", locked);
            await command.ExecuteScalarAsync(ct);
        }

        var blocked = fixture.SendAsUserAsync("cc-lock-owner", HttpMethod.Post, $"/api/v1/tenants/{locked}/memberships/{lockedStaff}/suspend");

        // Readers of the locked tenant, and writers of another tenant, proceed.
        var watch = Stopwatch.StartNew();
        using (var read = await fixture.SendAsUserAsync("cc-lock-owner", HttpMethod.Get, $"/api/v1/tenants/{locked}/memberships"))
        {
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }

        using (var otherWrite = await fixture.SendAsUserAsync("cc-lock-other-owner", HttpMethod.Post, $"/api/v1/tenants/{other}/memberships/{otherStaff}/suspend"))
        {
            Assert.Equal(HttpStatusCode.OK, otherWrite.StatusCode);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Unrelated work waited {watch.Elapsed}.");
        Assert.False(blocked.IsCompleted, "A writer of the locked tenant did not wait for the lock.");

        // Once the lock is released, the waiting change completes.
        await transaction.CommitAsync(ct);
        using var completed = await blocked;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(("MerchantStaff", "Suspended"), await fixture.StoredMembershipAsync(lockedStaff));
    }

    [Fact]
    public async Task ALockThatIsNeverReleased_FailsTheChangeWith503_AndChangesNothing()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("cc-timeout", "cc-timeout-owner");
        var staff = await fixture.SeedMemberAsync(tenantId, "cc-timeout-staff", MerchantRole.MerchantStaff);
        var ct = TestContext.Current.CancellationToken;

        await using var connection = new SqlConnection(fixture.Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        await using (var command = new SqlCommand("SELECT 1 FROM Tenants WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = @id", connection, transaction))
        {
            command.Parameters.AddWithValue("@id", tenantId);
            await command.ExecuteScalarAsync(ct);
        }

        using (var response = await fixture.SendAsUserAsync("cc-timeout-owner", HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships/{staff}/suspend"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
        }

        await transaction.RollbackAsync(ct);
        Assert.Equal(("MerchantStaff", "Active"), await fixture.StoredMembershipAsync(staff));
        Assert.Equal(0, await fixture.AuditCountAsync(tenantId, "MembershipSuspended"));
    }

    [Fact]
    public async Task ChangesOfManyTenants_InParallel_AllSucceed()
    {
        fixture.RequireAvailable();
        var targets = new List<(string Owner, Guid Tenant, Guid Staff)>();
        for (var index = 0; index < 12; index++)
        {
            var tenantId = await fixture.ProvisionTenantAsync($"cc-many-{index}", $"cc-many-owner-{index}");
            targets.Add(($"cc-many-owner-{index}", tenantId, await fixture.SeedMemberAsync(tenantId, $"cc-many-staff-{index}", MerchantRole.MerchantStaff)));
        }

        using var start = new Barrier(targets.Count);
        var responses = await Task.WhenAll(targets.Select(target =>
            Task.Run(() => ActAsync(target.Owner, target.Tenant, target.Staff, "remove", start))));

        Assert.All(responses, status => Assert.Equal(HttpStatusCode.OK, status));
        foreach (var target in targets)
        {
            Assert.Equal(("MerchantStaff", "Removed"), await fixture.StoredMembershipAsync(target.Staff));
        }
    }

    private async Task<HttpStatusCode> ActAsync(string actor, Guid tenantId, Guid target, string action, Barrier start)
    {
        using var client = fixture.CreateClient(fixture.UserToken(actor));
        using var request = action == "role"
            ? new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tenants/{tenantId}/memberships/{target}/role")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { role = "MerchantAdmin" }),
            }
            : new HttpRequestMessage(HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships/{target}/{action}");

        start.SignalAndWait(TimeSpan.FromSeconds(30));
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }
}
