using System.Net;
using System.Net.Http.Json;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// Invitation acceptance (ADR-021, decision D14): the token proves receipt, Identity confirms that the caller's verified
/// email is the invited address, the subject is the token's <c>sub</c>, and exactly one membership is ever created.
/// </summary>
public sealed class InvitationAcceptanceTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheInvitedUser_WithAVerifiedMatchingEmail_BecomesAMember()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-happy", "acc-happy-owner");
        var invitation = await fixture.InviteAsync(tenantId, "acc-happy-owner", "New.Hire@example.test", "MerchantAdmin");
        fixture.Identity.SetAccount("acc-happy-invitee", new StubAccount("new.hire@EXAMPLE.test"));
        var callsBefore = fixture.Identity.VerificationCalls;

        using var response = await fixture.AcceptAsync("acc-happy-invitee", invitation.AcceptanceToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var accepted = (await response.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal(tenantId, accepted.TenantId);
        Assert.Equal("acc-happy-invitee", accepted.Membership!.SubjectId);
        Assert.Equal("MerchantAdmin", accepted.Membership.Role);
        Assert.Equal("Active", accepted.Membership.Status);
        Assert.Equal($"/api/v1/tenants/{tenantId}/memberships/{accepted.Membership.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(callsBefore + 1, fixture.Identity.VerificationCalls);

        // The invitation records who accepted it and which membership it created; the change is audited.
        using var read = await fixture.SendAsUserAsync("acc-happy-owner", HttpMethod.Get, $"/api/v1/tenants/{tenantId}/invitations/{invitation.Invitation.Id}");
        var stored = (await read.Content.ReadFromJsonAsync<InvitationDto>(Ct))!;
        Assert.Equal("Accepted", stored.Status);
        Assert.Equal("acc-happy-invitee", stored.AcceptedBySubjectId);
        Assert.Equal(accepted.Membership.Id, stored.AcceptedMembershipId);
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "InvitationAccepted"));

        // The new member sees the tenant, in their own tenant list and directly.
        using var mine = await fixture.SendAsUserAsync("acc-happy-invitee", HttpMethod.Get, "/api/v1/tenants");
        var tenants = (await mine.Content.ReadFromJsonAsync<CallerTenantPageDto>(Ct))!;
        Assert.Equal([tenantId], tenants.Items.Select(t => t.Id));
        Assert.Equal("MerchantAdmin", tenants.Items[0].Membership.Role);
        using var tenant = await fixture.SendAsUserAsync("acc-happy-invitee", HttpMethod.Get, $"/api/v1/tenants/{tenantId}");
        Assert.Equal(HttpStatusCode.OK, tenant.StatusCode);
    }

    [Fact]
    public async Task ARepeatedAcceptance_IsAReplayForTheSameUser_AndRefusedForAnyoneElse()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-replay", "acc-replay-owner");
        var invitation = await fixture.InviteAsync(tenantId, "acc-replay-owner", "replay@example.test");
        fixture.Identity.SetAccount("acc-replay-invitee", new StubAccount("replay@example.test"));
        fixture.Identity.SetAccount("acc-replay-thief", new StubAccount("replay@example.test"));

        using var first = await fixture.AcceptAsync("acc-replay-invitee", invitation.AcceptanceToken);
        var membership = (await first.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!.Membership!;
        var callsAfterFirst = fixture.Identity.VerificationCalls;

        using var replay = await fixture.AcceptAsync("acc-replay-invitee", invitation.AcceptanceToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(membership.Id, (await replay.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!.Membership!.Id);
        Assert.Equal(callsAfterFirst, fixture.Identity.VerificationCalls);

        // Even someone whose verified email matches cannot reuse a used token.
        using var reuse = await fixture.AcceptAsync("acc-replay-thief", invitation.AcceptanceToken);
        (await ProvisioningApiTests.AssertProblemAsync(reuse, HttpStatusCode.Conflict, "invitation-not-pending")).Dispose();

        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE TenantId = '{tenantId}' AND Role <> N'MerchantOwner'"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "InvitationAccepted"));
    }

    [Theory]
    [InlineData("mismatch", "invitation-email-mismatch")]
    [InlineData("unverified", "invitation-email-unverified")]
    [InlineData("inactive", "invitation-account-ineligible")]
    [InlineData("unknown", "invitation-account-ineligible")]
    public async Task IdentitysVerdict_DecidesTheAcceptance_AndARefusalChangesNothing(string account, string code)
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync($"acc-verdict-{account}", $"acc-verdict-owner-{account}");
        var invitation = await fixture.InviteAsync(tenantId, $"acc-verdict-owner-{account}", $"{account}@example.test");
        var subject = $"acc-verdict-{account}";
        switch (account)
        {
            case "mismatch":
                fixture.Identity.SetAccount(subject, new StubAccount("someone.else@example.test"));
                break;
            case "unverified":
                fixture.Identity.SetAccount(subject, new StubAccount($"{account}@example.test", EmailConfirmed: false));
                break;
            case "inactive":
                fixture.Identity.SetAccount(subject, new StubAccount($"{account}@example.test", Active: false));
                break;
        }

        using var response = await fixture.AcceptAsync(subject, invitation.AcceptanceToken);

        (await ProvisioningApiTests.AssertProblemAsync(response, HttpStatusCode.Forbidden, code)).Dispose();
        Assert.Equal("Pending", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{invitation.Invitation.Id}'"));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE SubjectId = N'{subject}'"));
        Assert.Equal(0, await fixture.AuditCountAsync(tenantId, "InvitationAccepted"));

        // The intended invitee can still accept afterwards.
        fixture.Identity.SetAccount($"{subject}-real", new StubAccount($"{account}@example.test"));
        using var real = await fixture.AcceptAsync($"{subject}-real", invitation.AcceptanceToken);
        Assert.Equal(HttpStatusCode.Created, real.StatusCode);
    }

    [Fact]
    public async Task IdentityUnavailable_FailsClosed_AndARejectedServiceTokenIsRenewedOnce()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-identity-down", "acc-identity-down-owner");
        var invitation = await fixture.InviteAsync(tenantId, "acc-identity-down-owner", "down@example.test");
        fixture.Identity.SetAccount("acc-identity-down-invitee", new StubAccount("down@example.test"));

        foreach (var failure in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.Forbidden, HttpStatusCode.BadRequest })
        {
            fixture.Identity.FailWith = failure;
            try
            {
                using var response = await fixture.AcceptAsync("acc-identity-down-invitee", invitation.AcceptanceToken);
                (await ProvisioningApiTests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "identity-unavailable")).Dispose();
                Assert.NotNull(response.Headers.RetryAfter);
            }
            finally
            {
                fixture.Identity.FailWith = null;
            }
        }

        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Memberships WHERE SubjectId = N'acc-identity-down-invitee'"));

        // A 401 (an expired or revoked service token) renews the token once; the acceptance then succeeds.
        var tokensBefore = fixture.Identity.TokenRequests;
        fixture.Identity.RejectTokens = 1;
        using var renewed = await fixture.AcceptAsync("acc-identity-down-invitee", invitation.AcceptanceToken);
        Assert.Equal(HttpStatusCode.Created, renewed.StatusCode);
        Assert.Equal(tokensBefore + 1, fixture.Identity.TokenRequests);
    }

    [Fact]
    public async Task WithoutTheIdentityIntegration_AcceptanceFailsClosed()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-not-configured", "acc-not-configured-owner");
        var invitation = await fixture.InviteAsync(tenantId, "acc-not-configured-owner", "unconfigured@example.test");
        await using var factory = fixture.CreateFactory(fixture.Database.ConnectionString, builder =>
        {
            builder.UseSetting("TenantMerchant:IdentityVerification:BaseAddress", string.Empty);
            builder.UseSetting("TenantMerchant:IdentityVerification:PrivateKeyPath", string.Empty);
        });
        using var client = TenantMerchantApiFixture.CreateClient(factory, fixture.UserToken("acc-not-configured-invitee"));

        using var response = await client.PostAsJsonAsync("/api/v1/merchant-invitations/accept", new { token = invitation.AcceptanceToken }, Ct);

        (await ProvisioningApiTests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "identity-unavailable")).Dispose();
        Assert.Equal("Pending", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{invitation.Invitation.Id}'"));
    }

    [Fact]
    public async Task ClosedInvitations_AndUnknownTokens_AreRefused()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-closed", "acc-closed-owner");
        var cancelled = await fixture.InviteAsync(tenantId, "acc-closed-owner", "cancelled@example.test");
        var lapsed = await fixture.InviteAsync(tenantId, "acc-closed-owner", "lapsed@example.test");
        fixture.Identity.SetAccount("acc-closed-a", new StubAccount("cancelled@example.test"));
        fixture.Identity.SetAccount("acc-closed-b", new StubAccount("lapsed@example.test"));
        (await fixture.SendAsUserAsync("acc-closed-owner", HttpMethod.Post, $"/api/v1/tenants/{tenantId}/invitations/{cancelled.Invitation.Id}/cancel")).Dispose();
        await InvitationApiTests.LapseAsync(fixture, lapsed.Invitation.Id);

        using (var r = await fixture.AcceptAsync("acc-closed-a", cancelled.AcceptanceToken))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Conflict, "invitation-not-pending")).Dispose();
        }

        // An expired token is refused, and its expiry recorded and audited the first time it is presented.
        using (var r = await fixture.AcceptAsync("acc-closed-b", lapsed.AcceptanceToken))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Gone, "invitation-expired")).Dispose();
        }

        using (var r = await fixture.AcceptAsync("acc-closed-b", lapsed.AcceptanceToken))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Gone, "invitation-expired")).Dispose();
        }

        Assert.Equal("Expired", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{lapsed.Invitation.Id}'"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenantId, "InvitationExpired"));

        // Unknown, altered and malformed tokens all look the same.
        var altered = cancelled.AcceptanceToken[..^1] + (cancelled.AcceptanceToken[^1] == 'A' ? 'B' : 'A');
        foreach (var token in new[] { "ecmi_" + new string('A', 43), altered, "not-a-token", "ecmi_" + new string('!', 43) })
        {
            using var r = await fixture.AcceptAsync("acc-closed-a", token);
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.NotFound, "invitation-not-found")).Dispose();
        }

        using (var r = await fixture.SendAsUserAsync("acc-closed-a", HttpMethod.Post, "/api/v1/merchant-invitations/accept", new { }))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.BadRequest, "validation-failed")).Dispose();
        }

        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Memberships WHERE SubjectId IN (N'acc-closed-a', N'acc-closed-b')"));
    }

    [Fact]
    public async Task NothingInTheRequest_ChoosesTheSubjectOrTheTenant()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-forged", "acc-forged-owner");
        var otherTenant = await fixture.ProvisionTenantAsync("acc-forged-other", "acc-forged-other-owner");
        var invitation = await fixture.InviteAsync(tenantId, "acc-forged-owner", "forged@example.test");
        fixture.Identity.SetAccount("acc-forged-invitee", new StubAccount("forged@example.test"));

        foreach (var json in new[]
        {
            $$"""{"token":"{{invitation.AcceptanceToken}}","tenantId":"{{otherTenant}}"}""",
            $$"""{"token":"{{invitation.AcceptanceToken}}","subjectId":"acc-forged-other-owner"}""",
            $$"""{"token":"{{invitation.AcceptanceToken}}","email":"forged@example.test"}""",
            $$"""{"token":"{{invitation.AcceptanceToken}}","role":"MerchantOwner"}""",
        })
        {
            using var r = await fixture.SendAsUserAsync("acc-forged-invitee", HttpMethod.Post, "/api/v1/merchant-invitations/accept", new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }

        // A forged tenant or role claim changes nothing either: the membership is the invitation's.
        using var client = fixture.CreateClient(fixture.Tokens.CreateToken(TokenSpec.User("acc-forged-invitee") with
        {
            ExtraClaims = new Dictionary<string, object> { ["tenant_id"] = otherTenant.ToString(), ["role"] = "MerchantOwner" },
        }));
        using var accepted = await client.PostAsJsonAsync("/api/v1/merchant-invitations/accept", new { token = invitation.AcceptanceToken }, Ct);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        var membership = (await accepted.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!;
        Assert.Equal(tenantId, membership.TenantId);
        Assert.Equal("MerchantStaff", membership.Membership!.Role);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE TenantId = '{otherTenant}' AND SubjectId = N'acc-forged-invitee'"));

        // Only merchant-portal users accept; a service token or no token never does.
        using var service = fixture.CreateClient(fixture.AdminToken());
        using (var r = await service.PostAsJsonAsync("/api/v1/merchant-invitations/accept", new { token = invitation.AcceptanceToken }, Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        using var anonymous = fixture.CreateClient();
        using (var r = await anonymous.PostAsJsonAsync("/api/v1/merchant-invitations/accept", new { token = invitation.AcceptanceToken }, Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }
    }

    [Fact]
    public async Task ACurrentMember_CannotAcceptAgain_ButARemovedMemberNeedsAndGetsANewMembership()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-members", "acc-members-owner");
        await fixture.SeedMemberAsync(tenantId, "acc-members-suspended", MerchantRole.MerchantStaff, MembershipStatus.Suspended);
        var removedId = await fixture.SeedMemberAsync(tenantId, "acc-members-removed", MerchantRole.MerchantAdmin, MembershipStatus.Removed);
        fixture.Identity.SetAccount("acc-members-suspended", new StubAccount("suspended@example.test"));
        fixture.Identity.SetAccount("acc-members-removed", new StubAccount("removed@example.test"));
        var forSuspended = await fixture.InviteAsync(tenantId, "acc-members-owner", "suspended@example.test", "MerchantAdmin");
        var forRemoved = await fixture.InviteAsync(tenantId, "acc-members-owner", "removed@example.test");

        // A suspended member is still a member: an invitation never reactivates or re-roles a membership.
        using (var r = await fixture.AcceptAsync("acc-members-suspended", forSuspended.AcceptanceToken))
        {
            (await ProvisioningApiTests.AssertProblemAsync(r, HttpStatusCode.Conflict, "already-a-member")).Dispose();
        }

        Assert.Equal("Pending", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{forSuspended.Invitation.Id}'"));

        // A removed membership stays removed; the accepted invitation creates a new one.
        using var rejoined = await fixture.AcceptAsync("acc-members-removed", forRemoved.AcceptanceToken);
        Assert.Equal(HttpStatusCode.Created, rejoined.StatusCode);
        var membership = (await rejoined.Content.ReadFromJsonAsync<AcceptedInvitationDto>(Ct))!.Membership!;
        Assert.NotEqual(removedId, membership.Id);
        Assert.Equal("MerchantStaff", membership.Role);
        Assert.Equal(("MerchantAdmin", "Removed"), await fixture.StoredMembershipAsync(removedId));
    }

    [Fact]
    public async Task ConcurrentAcceptances_CreateExactlyOneMembership()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-race", "acc-race-owner");
        var sameUser = await fixture.InviteAsync(tenantId, "acc-race-owner", "same@example.test");
        var manyUsers = await fixture.InviteAsync(tenantId, "acc-race-owner", "many@example.test");
        fixture.Identity.SetAccount("acc-race-same", new StubAccount("same@example.test"));
        foreach (var index in Enumerable.Range(0, 8))
        {
            // Identity would never verify one address for eight accounts; the stub does, to stress the invitation alone.
            fixture.Identity.SetAccount($"acc-race-many-{index}", new StubAccount("many@example.test"));
        }

        var same = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => fixture.AcceptAsync("acc-race-same", sameUser.AcceptanceToken)));
        var many = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => fixture.AcceptAsync($"acc-race-many-{index}", manyUsers.AcceptanceToken)));

        // The same user: one creation, and every other attempt answered as a replay of it.
        Assert.Single(same, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, same.Count(r => r.StatusCode == HttpStatusCode.OK));

        // Different users: one winner, everyone else refused.
        Assert.Single(many, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, many.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        Assert.Equal(2, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE TenantId = '{tenantId}' AND Role = N'MerchantStaff'"));
        Assert.Equal(2, await fixture.AuditCountAsync(tenantId, "InvitationAccepted"));
        foreach (var response in same.Concat(many))
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task AFailedAuditWrite_RollsTheAcceptanceBack()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-audit-fail", "acc-audit-fail-owner");
        var invitation = await fixture.InviteAsync(tenantId, "acc-audit-fail-owner", "audit@example.test");
        fixture.Identity.SetAccount("acc-audit-fail-invitee", new StubAccount("audit@example.test"));

        await fixture.Database.ExecuteAsync("ALTER TABLE MembershipAuditRecords WITH NOCHECK ADD CONSTRAINT CK_Test_RejectAcceptanceAudit CHECK (Action <> N'InvitationAccepted')");
        try
        {
            using var response = await fixture.AcceptAsync("acc-audit-fail-invitee", invitation.AcceptanceToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            await fixture.Database.ExecuteAsync("ALTER TABLE MembershipAuditRecords DROP CONSTRAINT CK_Test_RejectAcceptanceAudit");
        }

        // Neither the membership nor the invitation's acceptance survived the failed audit write.
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Memberships WHERE SubjectId = N'acc-audit-fail-invitee'"));
        Assert.Equal("Pending", await fixture.Database.ScalarAsync<string>($"SELECT Status FROM MerchantInvitations WHERE Id = '{invitation.Invitation.Id}'"));

        using var retry = await fixture.AcceptAsync("acc-audit-fail-invitee", invitation.AcceptanceToken);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    [Fact]
    public async Task TokensAndAddresses_NeverReachTheLogs()
    {
        fixture.RequireAvailable();
        var tenantId = await fixture.ProvisionTenantAsync("acc-logs", "acc-logs-owner");
        var invitation = await fixture.InviteAsync(tenantId, "acc-logs-owner", "secret.person@example.test");
        fixture.Identity.SetAccount("acc-logs-wrong", new StubAccount("other.person@example.test"));
        fixture.Identity.SetAccount("acc-logs-invitee", new StubAccount("secret.person@example.test"));

        (await fixture.AcceptAsync("acc-logs-wrong", invitation.AcceptanceToken)).Dispose();
        (await fixture.AcceptAsync("acc-logs-invitee", invitation.AcceptanceToken)).Dispose();
        (await fixture.AcceptAsync("acc-logs-invitee", invitation.AcceptanceToken)).Dispose();

        var text = fixture.Logs.AllText().ToList();
        Assert.Contains(text, line => line.Contains("accepted by subject", StringComparison.Ordinal));
        Assert.DoesNotContain(text, line => line.Contains(invitation.AcceptanceToken, StringComparison.Ordinal));
        Assert.DoesNotContain(text, line => line.Contains("ecmi_", StringComparison.Ordinal));
        Assert.DoesNotContain(text, line => line.Contains("secret.person", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(text, line => line.Contains("other.person", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(text, line => line.Contains("stub-service-token-", StringComparison.Ordinal));
    }
}
