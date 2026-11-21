using System.Net;
using System.Net.Http.Json;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// Membership administration (ADR-021): the role matrix, self-modification, the lifecycle with <c>Removed</c> as a
/// terminal state, tenant scoping, and the audit record written with every change and only with a change.
/// </summary>
public sealed class MembershipAdministrationTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheOwner_RunsTheWholeLifecycle_AndEveryChangeIsAuditedOnce()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("adm-lifecycle", "adm-lifecycle-owner");
        var staff = await fixture.SeedMemberAsync(tenantId, "adm-lifecycle-staff", MerchantRole.MerchantStaff);
        var path = $"/api/v1/tenants/{tenantId}/memberships/{staff}";

        var suspended = await ChangeAsync("adm-lifecycle-owner", HttpMethod.Post, $"{path}/suspend", HttpStatusCode.OK);
        Assert.Equal("Suspended", suspended!.Status);

        // A suspended member is treated like a non-member at once: the next request sees nothing.
        using (var r = await fixture.SendAsUserAsync("adm-lifecycle-staff", HttpMethod.Get, $"/api/v1/tenants/{tenantId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        // Repeating a change that is in effect is answered with the current state, and writes and audits nothing.
        await ChangeAsync("adm-lifecycle-owner", HttpMethod.Post, $"{path}/suspend", HttpStatusCode.OK);
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "MembershipSuspended"));

        Assert.Equal("Active", (await ChangeAsync("adm-lifecycle-owner", HttpMethod.Post, $"{path}/reactivate", HttpStatusCode.OK))!.Status);
        Assert.Equal("MerchantAdmin", (await ChangeAsync("adm-lifecycle-owner", HttpMethod.Put, $"{path}/role", HttpStatusCode.OK, new { role = "MerchantAdmin" }))!.Role);
        Assert.Equal("MerchantStaff", (await ChangeAsync("adm-lifecycle-owner", HttpMethod.Put, $"{path}/role", HttpStatusCode.OK, new { role = "MerchantStaff" }))!.Role);
        Assert.Equal("Removed", (await ChangeAsync("adm-lifecycle-owner", HttpMethod.Post, $"{path}/remove", HttpStatusCode.OK))!.Status);

        // Removed is terminal: nothing brings the membership back.
        foreach (var (method, action, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "reactivate", null),
            (HttpMethod.Post, "suspend", null),
            (HttpMethod.Put, "role", new { role = "MerchantAdmin" }),
        })
        {
            using var r = await fixture.SendAsUserAsync("adm-lifecycle-owner", method, $"{path}/{action}", body);
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Conflict, "invalid-transition")).Dispose();
        }

        // The removed membership stays readable as history.
        using (var r = await fixture.SendAsUserAsync("adm-lifecycle-owner", HttpMethod.Get, path))
        {
            Assert.Equal("Removed", (await r.Content.ReadFromJsonAsync<MembershipDto>(Ct))!.Status);
        }

        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "MembershipSuspended"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "MembershipReactivated"));
        Assert.Equal(2, await fixture.AuditCountAsync(tenantId, "MembershipRoleChanged"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "MembershipRemoved"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "OwnerMembershipProvisioned"));
    }

    [Fact]
    public async Task TheRoleMatrix_IsEnforced()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("adm-matrix", "adm-matrix-owner");
        var owner = await fixture.MembershipIdAsync(tenantId, "adm-matrix-owner");
        var coOwner = await fixture.SeedMemberAsync(tenantId, "adm-matrix-co-owner", MerchantRole.MerchantOwner);
        var admin = await fixture.SeedMemberAsync(tenantId, "adm-matrix-admin", MerchantRole.MerchantAdmin);
        var otherAdmin = await fixture.SeedMemberAsync(tenantId, "adm-matrix-admin-2", MerchantRole.MerchantAdmin);
        var staff = await fixture.SeedMemberAsync(tenantId, "adm-matrix-staff", MerchantRole.MerchantStaff);
        var otherStaff = await fixture.SeedMemberAsync(tenantId, "adm-matrix-staff-2", MerchantRole.MerchantStaff);
        string Path(Guid membership, string action) => $"/api/v1/tenants/{tenantId}/memberships/{membership}/{action}";

        // Staff administer nothing.
        foreach (var target in new[] { otherStaff, admin, owner })
        {
            using var r = await fixture.SendAsUserAsync("adm-matrix-staff", HttpMethod.Post, Path(target, "suspend"));
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose();
        }

        using (var r = await fixture.SendAsUserAsync("adm-matrix-staff", HttpMethod.Get, $"/api/v1/tenants/{tenantId}/memberships/{otherStaff}"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose();
        }

        // An admin manages staff, and neither admins nor owners; an admin changes no roles.
        foreach (var target in new[] { otherAdmin, owner, coOwner })
        {
            foreach (var action in new[] { "suspend", "remove" })
            {
                using var r = await fixture.SendAsUserAsync("adm-matrix-admin", HttpMethod.Post, Path(target, action));
                (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose();
            }
        }

        using (var r = await fixture.SendAsUserAsync("adm-matrix-admin", HttpMethod.Put, Path(staff, "role"), new { role = "MerchantAdmin" }))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "permission-denied")).Dispose();
        }

        await ChangeAsync("adm-matrix-admin", HttpMethod.Post, Path(staff, "suspend"), HttpStatusCode.OK);
        await ChangeAsync("adm-matrix-admin", HttpMethod.Post, Path(staff, "reactivate"), HttpStatusCode.OK);

        // An owner manages everyone else, including another owner (whom it may demote, never promote anyone to).
        await ChangeAsync("adm-matrix-owner", HttpMethod.Post, Path(otherAdmin, "suspend"), HttpStatusCode.OK);
        await ChangeAsync("adm-matrix-owner", HttpMethod.Put, Path(coOwner, "role"), HttpStatusCode.OK, new { role = "MerchantAdmin" });
        using (var r = await fixture.SendAsUserAsync("adm-matrix-owner", HttpMethod.Put, Path(admin, "role"), new { role = "MerchantOwner" }))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.UnprocessableEntity, "owner-role-reserved")).Dispose();
        }

        Assert.Equal(("MerchantAdmin", "Active"), await fixture.StoredMembershipAsync(admin));
        Assert.Equal(("MerchantAdmin", "Active"), await fixture.StoredMembershipAsync(coOwner));
        Assert.Equal(1, await fixture.ActiveOwnersAsync(tenantId));
    }

    [Fact]
    public async Task NoMemberChangesTheirOwnMembership()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("adm-self", "adm-self-owner");
        var owner = await fixture.MembershipIdAsync(tenantId, "adm-self-owner");
        var admin = await fixture.SeedMemberAsync(tenantId, "adm-self-admin", MerchantRole.MerchantAdmin);
        string Path(Guid membership, string action) => $"/api/v1/tenants/{tenantId}/memberships/{membership}/{action}";

        foreach (var (method, action, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "suspend", null),
            (HttpMethod.Post, "remove", null),
            (HttpMethod.Put, "role", new { role = "MerchantStaff" }),
        })
        {
            using var r = await fixture.SendAsUserAsync("adm-self-owner", method, Path(owner, action), body);
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Forbidden, "self-modification-forbidden")).Dispose();
        }

        // An admin cannot raise itself either: role changes are an owner's, and owner is never assignable.
        foreach (var role in new[] { "MerchantAdmin", "MerchantOwner" })
        {
            using var r = await fixture.SendAsUserAsync("adm-self-admin", HttpMethod.Put, Path(admin, "role"), new { role });
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        Assert.Equal(("MerchantOwner", "Active"), await fixture.StoredMembershipAsync(owner));
        Assert.Equal(("MerchantAdmin", "Active"), await fixture.StoredMembershipAsync(admin));
        Assert.Equal(0, await fixture.AuditCountAsync(tenantId) - 1);
    }

    [Fact]
    public async Task AdministrationIsTenantScoped_AndTokenClaimsAreNoProof()
    {
        fixture.RequireAvailable();
        var tenantA = await fixture.ProvisionTenantAsync("adm-scope-a", "adm-scope-owner-a");
        var tenantB = await fixture.ProvisionTenantAsync("adm-scope-b", "adm-scope-owner-b");
        var staffOfB = await fixture.SeedMemberAsync(tenantB, "adm-scope-staff-b", MerchantRole.MerchantStaff);
        await fixture.SeedMemberAsync(tenantA, "adm-scope-staff-a", MerchantRole.MerchantStaff);

        // A's owner, through A's path with B's membership: not in A.
        using (var r = await fixture.SendAsUserAsync("adm-scope-owner-a", HttpMethod.Post, $"/api/v1/tenants/{tenantA}/memberships/{staffOfB}/suspend"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "membership-not-found")).Dispose();
        }

        // Through B's path: A's owner is no member of B.
        using (var r = await fixture.SendAsUserAsync("adm-scope-owner-a", HttpMethod.Post, $"/api/v1/tenants/{tenantB}/memberships/{staffOfB}/suspend"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "tenant-not-found")).Dispose();
        }

        // Forged role and tenant claims give staff of A nothing in A or B.
        using var forged = fixture.CreateClient(fixture.Tokens.CreateToken(TokenSpec.User("adm-scope-staff-a") with
        {
            ExtraClaims = new Dictionary<string, object> { ["role"] = "MerchantOwner", ["roles"] = new[] { "MerchantOwner" }, ["tenant_id"] = tenantB.ToString() },
        }));
        using (var r = await forged.PostAsync($"/api/v1/tenants/{tenantA}/memberships/{await fixture.MembershipIdAsync(tenantA, "adm-scope-owner-a")}/suspend", null, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        using (var r = await forged.PostAsync($"/api/v1/tenants/{tenantB}/memberships/{staffOfB}/suspend", null, Ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        // Service tokens are not merchant users.
        using var service = fixture.CreateClient(fixture.AdminToken());
        using (var r = await service.PostAsync($"/api/v1/tenants/{tenantB}/memberships/{staffOfB}/suspend", null, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        Assert.Equal(("MerchantStaff", "Active"), await fixture.StoredMembershipAsync(staffOfB));
    }

    [Fact]
    public async Task InvalidRoleRequests_AreRejected()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("adm-input", "adm-input-owner");
        var staff = await fixture.SeedMemberAsync(tenantId, "adm-input-staff", MerchantRole.MerchantStaff);
        var path = $"/api/v1/tenants/{tenantId}/memberships/{staff}/role";

        foreach (var body in new object[] { new { }, new { role = "Owner" }, new { role = "merchantadmin" }, new { role = "1" } })
        {
            using var r = await fixture.SendAsUserAsync("adm-input-owner", HttpMethod.Put, path, body);
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.BadRequest, "validation-failed")).Dispose();
        }

        using (var r = await fixture.SendAsUserAsync("adm-input-owner", HttpMethod.Put, path, new StringContent("""{"role":"MerchantAdmin","status":"Active"}""", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }

        using (var r = await fixture.SendAsUserAsync("adm-input-owner", HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships/not-a-guid/suspend"))
        {
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }

        using (var r = await fixture.SendAsUserAsync("adm-input-owner", HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships/{Guid.NewGuid()}/suspend"))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "membership-not-found")).Dispose();
        }

        Assert.Equal(("MerchantStaff", "Active"), await fixture.StoredMembershipAsync(staff));
    }

    [Fact]
    public async Task TheAuditRecord_HoldsTheActorTheChangeAndTheCorrelationId()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("adm-audit", "adm-audit-owner");
        var staff = await fixture.SeedMemberAsync(tenantId, "adm-audit-staff", MerchantRole.MerchantStaff);
        using var client = fixture.CreateClient(fixture.UserToken("adm-audit-owner"));
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/tenants/{tenantId}/memberships/{staff}/role")
        {
            Content = JsonContent.Create(new { role = "MerchantAdmin" }),
        };
        request.Headers.Add("X-Correlation-ID", "adm-audit-correlation-1");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var row = await fixture.Database.ScalarAsync<string>($"""
            SELECT CONCAT(ActorKind, '|', ActorId, '|', MembershipId, '|', SubjectId, '|', PreviousRole, '|', NewRole, '|', PreviousStatus, '|', NewStatus, '|', CorrelationId)
            FROM MembershipAuditRecords WHERE TenantId = '{tenantId}' AND Action = N'MembershipRoleChanged'
            """);
        Assert.Equal($"User|adm-audit-owner|{staff.ToString().ToUpperInvariant()}|adm-audit-staff|MerchantStaff|MerchantAdmin|Active|Active|adm-audit-correlation-1", row);

        var provisioned = await fixture.Database.ScalarAsync<string>(
            $"SELECT CONCAT(ActorKind, '|', ActorId, '|', NewRole) FROM MembershipAuditRecords WHERE TenantId = '{tenantId}' AND Action = N'OwnerMembershipProvisioned'");
        Assert.Equal($"Service|{TestTokenIssuer.AdminClient}|MerchantOwner", provisioned);
    }

    [Fact]
    public async Task AFailedAuditWrite_RollsTheChangeBack()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("adm-audit-fail", "adm-audit-fail-owner");
        var staff = await fixture.SeedMemberAsync(tenantId, "adm-audit-fail-staff", MerchantRole.MerchantStaff);

        await fixture.Database.ExecuteAsync("ALTER TABLE MembershipAuditRecords WITH NOCHECK ADD CONSTRAINT CK_Test_RejectSuspensionAudit CHECK (Action <> N'MembershipSuspended')");
        try
        {
            using var response = await fixture.SendAsUserAsync("adm-audit-fail-owner", HttpMethod.Post, $"/api/v1/tenants/{tenantId}/memberships/{staff}/suspend");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.NotNull(response.Headers.RetryAfter);
        }
        finally
        {
            await fixture.Database.ExecuteAsync("ALTER TABLE MembershipAuditRecords DROP CONSTRAINT CK_Test_RejectSuspensionAudit");
        }

        Assert.Equal(("MerchantStaff", "Active"), await fixture.StoredMembershipAsync(staff));
        Assert.Equal(0, await fixture.AuditCountAsync(tenantId, "MembershipSuspended"));
    }

    [Fact]
    public async Task CallerTenants_ListsOnlyActiveMemberships()
    {
        fixture.RequireAvailable();
        var first = await fixture.ProvisionTenantAsync("adm-mine-1", "adm-mine-owner-1");
        var second = await fixture.ProvisionTenantAsync("adm-mine-2", "adm-mine-owner-2");
        var third = await fixture.ProvisionTenantAsync("adm-mine-3", "adm-mine-owner-3");
        await fixture.SeedMemberAsync(first, "adm-mine-user", MerchantRole.MerchantStaff);
        await fixture.SeedMemberAsync(second, "adm-mine-user", MerchantRole.MerchantAdmin, MembershipStatus.Suspended);
        await fixture.SeedMemberAsync(third, "adm-mine-user", MerchantRole.MerchantAdmin, MembershipStatus.Removed);

        using var response = await fixture.SendAsUserAsync("adm-mine-user", HttpMethod.Get, "/api/v1/tenants");
        var page = (await response.Content.ReadFromJsonAsync<CallerTenantPageDto>(Ct))!;
        Assert.Equal([first], page.Items.Select(t => t.Id));
        Assert.Equal("adm-mine-1", page.Items[0].Slug);
        Assert.Equal(1, page.TotalCount);

        using var none = await fixture.SendAsUserAsync("adm-mine-nobody", HttpMethod.Get, "/api/v1/tenants");
        Assert.Equal(0, (await none.Content.ReadFromJsonAsync<CallerTenantPageDto>(Ct))!.TotalCount);

        using var invalid = await fixture.SendAsUserAsync("adm-mine-user", HttpMethod.Get, "/api/v1/tenants?pageSize=0");
        (await ProvisioningApiTests.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation-failed")).Dispose();
    }

    [Fact]
    public async Task TheDatabase_RefusesToLeaveATenantWithoutAnOwner_AndToChangeAudit()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("adm-trigger", "adm-trigger-owner");
        var owner = await fixture.MembershipIdAsync(tenantId, "adm-trigger-owner");

        // A path that bypasses the application's rules (here, plain SQL) still cannot remove the last owner.
        foreach (var statement in new[]
        {
            $"UPDATE Memberships SET Status = N'Suspended' WHERE Id = '{owner}'",
            $"UPDATE Memberships SET Role = N'MerchantAdmin' WHERE Id = '{owner}'",
            $"DELETE FROM Memberships WHERE Id = '{owner}'",
        })
        {
            var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => fixture.Database.ExecuteAsync(statement));
            Assert.Equal(50001, error.Number);
        }

        // With a second active owner, the same statement is allowed.
        await fixture.SeedMemberAsync(tenantId, "adm-trigger-co-owner", MerchantRole.MerchantOwner);
        await fixture.Database.ExecuteAsync($"UPDATE Memberships SET Status = N'Suspended' WHERE Id = '{owner}'");
        Assert.Equal(1, await fixture.ActiveOwnersAsync(tenantId));

        foreach (var statement in new[]
        {
            $"UPDATE MembershipAuditRecords SET ActorId = N'someone-else' WHERE TenantId = '{tenantId}'",
            $"DELETE FROM MembershipAuditRecords WHERE TenantId = '{tenantId}'",
        })
        {
            var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => fixture.Database.ExecuteAsync(statement));
            Assert.Equal(50002, error.Number);
        }

        Assert.Equal(1, await fixture.AuditCountAsync(tenantId));
    }

    private async Task<MembershipDto?> ChangeAsync(string caller, HttpMethod method, string path, HttpStatusCode expected, object? body = null)
    {
        using var response = await fixture.SendAsUserAsync(caller, method, path, body);
        Assert.Equal(expected, response.StatusCode);
        return expected == HttpStatusCode.OK ? await response.Content.ReadFromJsonAsync<MembershipDto>(Ct) : null;
    }
}
