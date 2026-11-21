using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// Invitation creation, reads and cancellation (ADR-021): the role policy, one pending invitation per address, the
/// lifecycle, and that the acceptance token is returned exactly once and stored only as a hash.
/// </summary>
public sealed class InvitationApiTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_ReturnsTheTokenOnce_AndStoresOnlyItsHash()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("inv-token-once", "inv-token-owner");

        using var response = await fixture.SendAsUserAsync(
            "inv-token-owner", HttpMethod.Post, $"/api/v1/tenants/{tenantId}/invitations", new { email = "Staff.One@Example.test", role = "MerchantStaff" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var created = (await response.Content.ReadFromJsonAsync<CreatedInvitationDto>(Ct))!;
        Assert.Equal($"/api/v1/tenants/{tenantId}/invitations/{created.Invitation.Id}", response.Headers.Location?.OriginalString);
        Assert.StartsWith("ecmi_", created.AcceptanceToken, StringComparison.Ordinal);
        Assert.Equal(48, created.AcceptanceToken.Length);
        Assert.Equal("Pending", created.Invitation.Status);
        Assert.Equal("MerchantStaff", created.Invitation.Role);
        Assert.Equal("inv-token-owner", created.Invitation.InvitedBySubjectId);
        Assert.True(created.Invitation.ExpiresAt > created.Invitation.CreatedAt.AddDays(6));

        // Neither the single read nor the list ever carries the token.
        using var read = await fixture.SendAsUserAsync("inv-token-owner", HttpMethod.Get, $"/api/v1/tenants/{tenantId}/invitations/{created.Invitation.Id}");
        using var list = await fixture.SendAsUserAsync("inv-token-owner", HttpMethod.Get, $"/api/v1/tenants/{tenantId}/invitations");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(created.AcceptanceToken, await read.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(created.AcceptanceToken, await list.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain("tokenHash", await read.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);

        // Stored: the SHA-256 hash only; the raw token in no column of any table.
        var storedHash = await fixture.Database.ScalarAsync<byte[]>($"SELECT TokenHash FROM MerchantInvitations WHERE Id = '{created.Invitation.Id}'");
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(created.AcceptanceToken)), storedHash);
        Assert.Equal(0, await CountRowsContainingAsync(created.AcceptanceToken));

        // Audited, without the address.
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "InvitationCreated"));
        Assert.Equal(0, await CountRowsContainingAsync("Staff.One@Example.test", tables: "MembershipAuditRecords"));
        Assert.DoesNotContain(fixture.Logs.AllText(), text => text.Contains(created.AcceptanceToken, StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Logs.AllText(), text => text.Contains("Staff.One@Example.test", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Create_FollowsTheRolePolicy()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("inv-policy", "inv-policy-owner");
        await fixture.SeedMemberAsync(tenantId, "inv-policy-admin", MerchantRole.MerchantAdmin);
        await fixture.SeedMemberAsync(tenantId, "inv-policy-staff", MerchantRole.MerchantStaff);
        await fixture.SeedMemberAsync(tenantId, "inv-policy-suspended", MerchantRole.MerchantAdmin, MembershipStatus.Suspended);
        var path = $"/api/v1/tenants/{tenantId}/invitations";

        async Task<HttpResponseMessage> Invite(string caller, string role) =>
            await fixture.SendAsUserAsync(caller, HttpMethod.Post, path, new { email = $"{caller}-{role}@example.test", role });

        using (var r = await Invite("inv-policy-owner", "MerchantAdmin")) { Assert.Equal(HttpStatusCode.Created, r.StatusCode); }
        using (var r = await Invite("inv-policy-owner", "MerchantStaff")) { Assert.Equal(HttpStatusCode.Created, r.StatusCode); }
        using (var r = await Invite("inv-policy-admin", "MerchantStaff")) { Assert.Equal(HttpStatusCode.Created, r.StatusCode); }
        using (var r = await Invite("inv-policy-admin", "MerchantAdmin")) { (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose(); }
        using (var r = await Invite("inv-policy-staff", "MerchantStaff")) { (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose(); }

        // Nobody, not even an owner, invites an owner: the role is reserved whoever asks.
        using (var r = await Invite("inv-policy-owner", "MerchantOwner")) { (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.UnprocessableEntity, "owner-role-reserved")).Dispose(); }
        using (var r = await Invite("inv-policy-admin", "MerchantOwner")) { (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.UnprocessableEntity, "owner-role-reserved")).Dispose(); }
        using (var r = await Invite("inv-policy-staff", "MerchantOwner")) { (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose(); }

        // A suspended member and a stranger see no tenant at all.
        using (var r = await Invite("inv-policy-suspended", "MerchantStaff")) { (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "tenant-not-found")).Dispose(); }
        using (var r = await Invite("inv-policy-stranger", "MerchantStaff")) { (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "tenant-not-found")).Dispose(); }

        Assert.Equal(3, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM MerchantInvitations WHERE TenantId = '{tenantId}'"));
    }

    [Fact]
    public async Task Create_RejectsInvalidAndUnknownInput()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("inv-input", "inv-input-owner");
        var path = $"/api/v1/tenants/{tenantId}/invitations";

        foreach (var (body, field) in new (object Body, string Field)[]
        {
            (new { role = "MerchantStaff" }, "email"),
            (new { email = "not-an-address", role = "MerchantStaff" }, "email"),
            (new { email = "Alice <alice@example.test>", role = "MerchantStaff" }, "email"),
            (new { email = "alice@localhost", role = "MerchantStaff" }, "email"),
            (new { email = "alice@example.test" }, "role"),
            (new { email = "alice@example.test", role = "merchantstaff" }, "role"),
            (new { email = "alice@example.test", role = "1" }, "role"),
        })
        {
            using var response = await fixture.SendAsUserAsync("inv-input-owner", HttpMethod.Post, path, body);
            using var problem = await ProvisioningApiTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), field);
        }

        // A field the contract does not define (a tenant, a status, a token) is refused, not ignored.
        foreach (var json in new[]
        {
            """{"email":"a@example.test","role":"MerchantStaff","tenantId":"00000000-0000-0000-0000-000000000001"}""",
            """{"email":"a@example.test","role":"MerchantStaff","status":"Accepted"}""",
            """{"email":"a@example.test","role":"MerchantStaff","acceptanceToken":"x"}""",
        })
        {
            using var response = await fixture.SendAsUserAsync("inv-input-owner", HttpMethod.Post, path, new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM MerchantInvitations WHERE TenantId = '{tenantId}'"));
    }

    [Fact]
    public async Task OnePendingInvitationPerAddress_ReinvitingAfterCancelOrExpiry_CreatesANewOne()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("inv-dupe", "inv-dupe-owner");
        var otherTenant = await fixture.ProvisionTenantAsync("inv-dupe-other", "inv-dupe-other-owner");
        var path = $"/api/v1/tenants/{tenantId}/invitations";
        var first = await fixture.InviteAsync(tenantId, "inv-dupe-owner", "dupe@example.test");

        // The same address in another letter case is the same address.
        using (var again = await fixture.SendAsUserAsync("inv-dupe-owner", HttpMethod.Post, path, new { email = "DUPE@example.TEST", role = "MerchantAdmin" }))
        {
            using var problem = await ProvisioningApiTests.AssertProblemAsync(again, HttpStatusCode.Conflict, "invitation-already-pending");
            Assert.Equal(first.Invitation.Id, problem.RootElement.GetProperty("invitationId").GetGuid());
        }

        // Another tenant may invite the same address.
        await fixture.InviteAsync(otherTenant, "inv-dupe-other-owner", "dupe@example.test");

        // After cancellation: a new invitation with a new token.
        using (var cancel = await fixture.SendAsUserAsync("inv-dupe-owner", HttpMethod.Post, $"{path}/{first.Invitation.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        var second = await fixture.InviteAsync(tenantId, "inv-dupe-owner", "dupe@example.test");
        Assert.NotEqual(first.AcceptanceToken, second.AcceptanceToken);

        // After expiry: the lapsed invitation is closed (and audited) in the same transaction as the new one.
        await LapseAsync(second.Invitation.Id);
        var third = await fixture.InviteAsync(tenantId, "inv-dupe-owner", "dupe@example.test");
        Assert.Equal("Expired", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{second.Invitation.Id}'"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "InvitationExpired"));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM MerchantInvitations WHERE TenantId = '{tenantId}' AND Status = N'Pending' AND Id = '{third.Invitation.Id}'"));
    }

    [Fact]
    public async Task ConcurrentCreation_ForOneAddress_CreatesExactlyOneInvitation()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("inv-race", "inv-race-owner");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.SendAsUserAsync(
            "inv-race-owner", HttpMethod.Post, $"/api/v1/tenants/{tenantId}/invitations", new { email = "race@example.test", role = "MerchantStaff" })));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM MerchantInvitations WHERE TenantId = '{tenantId}'"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "InvitationCreated"));
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Cancel_FollowsTheRolePolicyAndTheLifecycle()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("inv-cancel", "inv-cancel-owner");
        await fixture.SeedMemberAsync(tenantId, "inv-cancel-admin", MerchantRole.MerchantAdmin);
        await fixture.SeedMemberAsync(tenantId, "inv-cancel-staff", MerchantRole.MerchantStaff);
        var path = $"/api/v1/tenants/{tenantId}/invitations";
        var adminInvite = await fixture.InviteAsync(tenantId, "inv-cancel-owner", "admin-to-be@example.test", "MerchantAdmin");
        var staffInvite = await fixture.InviteAsync(tenantId, "inv-cancel-owner", "staff-to-be@example.test");

        // An admin cannot cancel what only an owner could create; staff cancel nothing.
        using (var r = await fixture.SendAsUserAsync("inv-cancel-admin", HttpMethod.Post, $"{path}/{adminInvite.Invitation.Id}/cancel"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose();
        }

        using (var r = await fixture.SendAsUserAsync("inv-cancel-staff", HttpMethod.Post, $"{path}/{staffInvite.Invitation.Id}/cancel"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose();
        }

        using (var r = await fixture.SendAsUserAsync("inv-cancel-admin", HttpMethod.Post, $"{path}/{staffInvite.Invitation.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var cancelled = (await r.Content.ReadFromJsonAsync<InvitationDto>(Ct))!;
            Assert.Equal("Cancelled", cancelled.Status);
            Assert.Equal("inv-cancel-admin", cancelled.CancelledBySubjectId);
        }

        // Cancelling again changes nothing and audits nothing.
        using (var r = await fixture.SendAsUserAsync("inv-cancel-owner", HttpMethod.Post, $"{path}/{staffInvite.Invitation.Id}/cancel"))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("inv-cancel-admin", (await r.Content.ReadFromJsonAsync<InvitationDto>(Ct))!.CancelledBySubjectId);
        }

        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "InvitationCancelled"));

        // A lapsed invitation is not cancelled: its expiry is recorded and 410 answered.
        await LapseAsync(adminInvite.Invitation.Id);
        using (var r = await fixture.SendAsUserAsync("inv-cancel-owner", HttpMethod.Post, $"{path}/{adminInvite.Invitation.Id}/cancel"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Gone, "invitation-expired")).Dispose();
        }

        Assert.Equal("Expired", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{adminInvite.Invitation.Id}'"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "InvitationExpired"));
    }

    [Fact]
    public async Task ReadsAndCancellation_AreTenantScoped()
    {
        fixture.RequireAvailable();
        var tenantA = await fixture.ProvisionTenantAsync("inv-scope-a", "inv-scope-owner-a");
        var tenantB = await fixture.ProvisionTenantAsync("inv-scope-b", "inv-scope-owner-b");
        await fixture.SeedMemberAsync(tenantA, "inv-scope-staff", MerchantRole.MerchantStaff);
        var invitation = await fixture.InviteAsync(tenantA, "inv-scope-owner-a", "scoped@example.test");

        // Tenant B's owner, through tenant B's path: the invitation of A does not exist there.
        using (var r = await fixture.SendAsUserAsync("inv-scope-owner-b", HttpMethod.Get, $"/api/v1/tenants/{tenantB}/invitations/{invitation.Invitation.Id}"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "invitation-not-found")).Dispose();
        }

        using (var r = await fixture.SendAsUserAsync("inv-scope-owner-b", HttpMethod.Post, $"/api/v1/tenants/{tenantB}/invitations/{invitation.Invitation.Id}/cancel"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "invitation-not-found")).Dispose();
        }

        // Through tenant A's path: B's owner is no member of A.
        using (var r = await fixture.SendAsUserAsync("inv-scope-owner-b", HttpMethod.Get, $"/api/v1/tenants/{tenantA}/invitations"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "tenant-not-found")).Dispose();
        }

        // Staff of A may not read A's invitations.
        using (var r = await fixture.SendAsUserAsync("inv-scope-staff", HttpMethod.Get, $"/api/v1/tenants/{tenantA}/invitations"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose();
        }

        Assert.Equal("Pending", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{invitation.Invitation.Id}'"));
    }

    [Fact]
    public async Task List_FiltersByEffectiveStatus_AndPages()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("inv-list", "inv-list-owner");
        var path = $"/api/v1/tenants/{tenantId}/invitations";
        var open = await fixture.InviteAsync(tenantId, "inv-list-owner", "open@example.test");
        var lapsed = await fixture.InviteAsync(tenantId, "inv-list-owner", "lapsed@example.test");
        var cancelled = await fixture.InviteAsync(tenantId, "inv-list-owner", "cancelled@example.test");
        await LapseAsync(lapsed.Invitation.Id);
        (await fixture.SendAsUserAsync("inv-list-owner", HttpMethod.Post, $"{path}/{cancelled.Invitation.Id}/cancel")).Dispose();

        async Task<InvitationPageDto> ListAsync(string query)
        {
            using var response = await fixture.SendAsUserAsync("inv-list-owner", HttpMethod.Get, path + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<InvitationPageDto>(Ct))!;
        }

        Assert.Equal([open.Invitation.Id], (await ListAsync("?status=Pending")).Items.Select(i => i.Id));

        // A lapsed pending invitation reads as expired, although its stored status is still pending.
        var expired = await ListAsync("?status=Expired");
        Assert.Equal([lapsed.Invitation.Id], expired.Items.Select(i => i.Id));
        Assert.Equal("Expired", expired.Items[0].Status);
        Assert.Equal("Pending", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{lapsed.Invitation.Id}'"));

        Assert.Equal([cancelled.Invitation.Id], (await ListAsync("?status=Cancelled")).Items.Select(i => i.Id));

        // Newest first, paged (the lapsed invitation was dated back by eight days).
        var all = await ListAsync("?page=1&pageSize=2");
        Assert.Equal(3, all.TotalCount);
        Assert.Equal([cancelled.Invitation.Id, open.Invitation.Id], all.Items.Select(i => i.Id));
        Assert.Equal([lapsed.Invitation.Id], (await ListAsync("?page=2&pageSize=2")).Items.Select(i => i.Id));

        foreach (var query in new[] { "?status=pending", "?status=Unknown", "?pageSize=101", "?page=0" })
        {
            using var response = await fixture.SendAsUserAsync("inv-list-owner", HttpMethod.Get, path + query);
            (await ProvisioningApiTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed")).Dispose();
        }
    }

    /// <summary>Moves an invitation's expiry into the past, as the authoritative clock would see it after its lifetime.</summary>
    internal static Task LapseAsync(TenantMerchantApiFixture fixture, Guid invitationId) =>
        fixture.Database.ExecuteAsync(
            $"UPDATE MerchantInvitations SET CreatedAt = DATEADD(day, -8, SYSDATETIMEOFFSET()), ExpiresAt = DATEADD(minute, -1, SYSDATETIMEOFFSET()) WHERE Id = '{invitationId}'");

    private Task LapseAsync(Guid invitationId) => LapseAsync(fixture, invitationId);

    /// <summary>Counts rows of the service's tables in which any string column contains <paramref name="value"/>.</summary>
    private async Task<int> CountRowsContainingAsync(string value, string tables = "Tenants,MerchantProfiles,Memberships,MerchantInvitations,MembershipAuditRecords")
    {
        var total = 0;
        foreach (var table in tables.Split(','))
        {
            var columns = await fixture.Database.ScalarAsync<string>(
                $"SELECT STRING_AGG(QUOTENAME(name), ',') FROM sys.columns WHERE object_id = OBJECT_ID(N'{table}') AND system_type_id IN (167, 175, 231, 239)");
            var condition = string.Join(" OR ", columns.Split(',').Select(column => $"{column} LIKE N'%{value.Replace("'", "''", StringComparison.Ordinal)}%'"));
            total += await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM [{table}] WHERE {condition}");
        }

        return total;
    }
}
