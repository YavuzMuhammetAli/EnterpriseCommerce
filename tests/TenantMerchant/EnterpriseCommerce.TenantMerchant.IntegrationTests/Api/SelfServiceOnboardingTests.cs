using System.Net;
using System.Net.Http.Json;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// Self-service onboarding (<c>POST /api/v1/tenants</c>, PHASE_3_S3_SELF_SERVICE_ONBOARDING.md) against the real host
/// and a real SQL Server database: the caller always becomes the owner, keys are scoped per subject, and the
/// per-subject limit holds.
/// </summary>
public sealed class SelfServiceOnboardingTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    [Fact]
    public async Task Create_MakesTheCallerTheOwnerOfAnActiveSelfServiceTenant()
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        var correlationId = $"corr-{Guid.NewGuid():N}";

        using var response = await CreateAsync(fixture.CreateClient(fixture.UserToken(subject)), "onboarded", configure: request => request.Headers.Add("X-Correlation-ID", correlationId));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = (await response.Content.ReadFromJsonAsync<MemberTenantDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(new Uri($"/api/v1/tenants/{tenant.Id}", UriKind.Relative), response.Headers.Location);
        Assert.Equal(("onboarded", "Active", "onboarded store"), (tenant.Slug, tenant.Status, tenant.MerchantProfile.DisplayName));
        Assert.Equal((subject, "MerchantOwner", "Active"), (tenant.Membership.SubjectId, tenant.Membership.Role, tenant.Membership.Status));

        Assert.Equal($"SelfService|{subject}|bff-merchant|{subject}", await fixture.Database.ScalarAsync<string>(
            $"SELECT CONCAT(Origin, '|', SelfServiceSubjectId, '|', ProvisionedByClientId, '|', ProvisionedByActor) FROM Tenants WHERE Id = '{tenant.Id}'"));
        Assert.Equal($"SelfServiceOwnerMembershipCreated|User|{subject}|{subject}|{tenant.Membership.Id}|{correlationId}", await fixture.Database.ScalarAsync<string>(
            $"SELECT CONCAT(Action, '|', ActorKind, '|', ActorId, '|', SubjectId, '|', LOWER(CONVERT(nvarchar(36), MembershipId)), '|', CorrelationId) FROM MembershipAuditRecords WHERE TenantId = '{tenant.Id}'"));
        Assert.Equal(1, await fixture.AuditCountAsync(tenant.Id));
    }

    [Fact]
    public async Task TheNewOwner_SeesTheTenant_AndCanAdministerIt()
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        using var created = await CreateAsync(fixture.CreateClient(fixture.UserToken(subject)), "administered");
        var tenant = (await created.Content.ReadFromJsonAsync<MemberTenantDto>(TestContext.Current.CancellationToken))!;

        using var mine = await fixture.SendAsUserAsync(subject, HttpMethod.Get, "/api/v1/tenants");
        var page = (await mine.Content.ReadFromJsonAsync<CallerTenantPageDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(tenant.Id, Assert.Single(page.Items).Id);
        Assert.Equal("MerchantOwner", page.Items[0].Membership.Role);

        using var read = await fixture.SendAsUserAsync(subject, HttpMethod.Get, $"/api/v1/tenants/{tenant.Id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var invitation = await fixture.InviteAsync(tenant.Id, subject, $"{subject}-admin@example.test", "MerchantAdmin");
        Assert.Equal("Pending", invitation.Invitation.Status);

        using var stranger = await fixture.SendAsUserAsync(NewSubject(), HttpMethod.Get, $"/api/v1/tenants/{tenant.Id}");
        await ProvisioningApiTests.AssertProblemAsync(stranger, HttpStatusCode.NotFound, "tenant-not-found");
    }

    public static TheoryData<string, HttpStatusCode> CallersWhoCannotOnboard() => new()
    {
        { "anonymous", HttpStatusCode.Unauthorized },
        { "service", HttpStatusCode.Forbidden },
        { "storefront-user", HttpStatusCode.Unauthorized },
        { "user-of-a-service-client", HttpStatusCode.Forbidden },
        { "user-without-client", HttpStatusCode.Forbidden },
        { "service-audience-user", HttpStatusCode.Forbidden },
        { "id-token-type", HttpStatusCode.Unauthorized },
        { "unknown-key", HttpStatusCode.Unauthorized },
    };

    [Theory]
    [MemberData(nameof(CallersWhoCannotOnboard))]
    public async Task OnlyMerchantPortalUsers_CanOnboard(string caller, HttpStatusCode expected)
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        var token = caller switch
        {
            "anonymous" => null,
            "service" => fixture.AdminToken(),
            "storefront-user" => fixture.Tokens.CreateToken(TokenSpec.User(subject) with { Audiences = ["ec-storefront"] }),
            "user-of-a-service-client" => fixture.Tokens.CreateToken(TokenSpec.User(subject) with { ClientId = "svc-test-admin" }),
            "user-without-client" => fixture.Tokens.CreateToken(TokenSpec.User(subject) with { ClientId = null }),
            "service-audience-user" => fixture.Tokens.CreateToken(TokenSpec.User(subject) with { Audiences = [TestTokenIssuer.ServiceAudience] }),
            "id-token-type" => fixture.Tokens.CreateToken(TokenSpec.User(subject) with { TokenType = "JWT" }),
            "unknown-key" => fixture.Tokens.CreateToken(TokenSpec.User(subject) with { SignWithUnknownKey = true }),
            _ => throw new ArgumentOutOfRangeException(nameof(caller)),
        };
        var slug = $"refused-{caller}";

        using var response = await CreateAsync(fixture.CreateClient(token), slug);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Tenants WHERE Slug = '{slug}'"));
    }

    [Fact]
    public async Task ForgedRoleAndTenantClaims_ChangeNothing_TheOwnerIsTheTokenSubject()
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        var token = fixture.Tokens.CreateToken(TokenSpec.User(subject) with
        {
            ExtraClaims = new Dictionary<string, object>
            {
                ["role"] = "MerchantStaff",
                ["tenant_id"] = Guid.NewGuid().ToString("D"),
                ["owner_sub"] = "attacker",
            },
        });

        using var response = await CreateAsync(fixture.CreateClient(token), "forged-claims");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenant = (await response.Content.ReadFromJsonAsync<MemberTenantDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal((subject, "MerchantOwner"), (tenant.Membership.SubjectId, tenant.Membership.Role));
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE TenantId = '{tenant.Id}'"));
    }

    [Theory]
    [InlineData("""{"slug":"smuggled","displayName":"x","ownerSubjectId":"attacker"}""")]
    [InlineData("""{"slug":"smuggled","displayName":"x","role":"MerchantAdmin"}""")]
    [InlineData("""{"slug":"smuggled","displayName":"x","memberships":[{"subjectId":"attacker","role":"MerchantOwner"}]}""")]
    [InlineData("""{"slug":"smuggled","displayName":"x","origin":"Provisioned","status":"Active","id":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("not json")]
    [InlineData("""["smuggled"]""")]
    public async Task FieldsOutsideTheContract_AndMalformedBodies_Are400(string json)
    {
        fixture.RequireAvailable();

        using var response = await CreateAsync(
            fixture.CreateClient(fixture.UserToken(NewSubject())),
            content: new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'smuggled'"));
    }

    [Theory]
    [InlineData("Not-A-Slug", "Fine", "slug")]
    [InlineData("ab", "Fine", "slug")]
    [InlineData("a--b", "Fine", "slug")]
    [InlineData("-leading", "Fine", "slug")]
    [InlineData("fine-slug", "", "displayName")]
    [InlineData("fine-slug", "bad\u0007name", "displayName")]
    public async Task InvalidFields_Are400PerField(string slug, string displayName, string field)
    {
        fixture.RequireAvailable();

        using var response = await CreateAsync(fixture.CreateClient(fixture.UserToken(NewSubject())), slug, displayName: displayName);

        using var problem = await ProvisioningApiTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal([field], problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task AnOversizedDisplayNameAndAMissingBody_Are400()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.UserToken(NewSubject()));

        using var oversized = await CreateAsync(client, "oversized", displayName: new string('x', 201));
        using var missing = await CreateAsync(client, content: new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        await ProvisioningApiTests.AssertProblemAsync(oversized, HttpStatusCode.BadRequest, "validation-failed");
        using var problem = await ProvisioningApiTests.AssertProblemAsync(missing, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal(["displayName", "slug"], problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/not-allowed")]
    public async Task WithoutAValidIdempotencyKey_Is400(string? key)
    {
        fixture.RequireAvailable();

        using var response = await CreateAsync(fixture.CreateClient(fixture.UserToken(NewSubject())), "no-key", key: key ?? string.Empty, omitKey: key is null);

        using var problem = await ProvisioningApiTests.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'no-key'"));
    }

    [Fact]
    public async Task ARetryWithTheSameKey_ReturnsTheOriginalTenant()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.UserToken(NewSubject()));
        var key = Guid.NewGuid().ToString("D");

        using var first = await CreateAsync(client, "retried-once", key: key);
        using var retry = await CreateAsync(client, "retried-once", key: key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Null(retry.Headers.Location);
        var original = (await first.Content.ReadFromJsonAsync<MemberTenantDto>(TestContext.Current.CancellationToken))!;
        var replayed = (await retry.Content.ReadFromJsonAsync<MemberTenantDto>(TestContext.Current.CancellationToken))!;
        Assert.Equal(original, replayed);
        Assert.Equal(1, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'retried-once'"));
        Assert.Equal(1, await fixture.AuditCountAsync(original.Id));
    }

    [Fact]
    public async Task TheSameKeyWithADifferentRequest_Is422()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.UserToken(NewSubject()));
        var key = Guid.NewGuid().ToString("D");
        using var first = await CreateAsync(client, "key-reused", key: key);

        using var reused = await CreateAsync(client, "key-reused", key: key, displayName: "Another name");

        await ProvisioningApiTests.AssertProblemAsync(reused, HttpStatusCode.UnprocessableEntity, "idempotency-key-reused");
    }

    [Fact]
    public async Task TwoUsersWithTheSameKey_GetTheirOwnTenants_NeverTheOther()
    {
        fixture.RequireAvailable();
        var (alice, bob) = (NewSubject(), NewSubject());
        const string key = "shared-key-of-two-users";

        using var aliceResponse = await CreateAsync(fixture.CreateClient(fixture.UserToken(alice)), $"alice-{alice[^8..]}", key: key);
        using var bobResponse = await CreateAsync(fixture.CreateClient(fixture.UserToken(bob)), $"bob-{bob[^8..]}", key: key);
        using var bobWithAlicesBody = await CreateAsync(fixture.CreateClient(fixture.UserToken(bob)), $"alice-{alice[^8..]}", key: key);

        Assert.Equal(HttpStatusCode.Created, aliceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bobResponse.StatusCode);
        var aliceTenant = (await aliceResponse.Content.ReadFromJsonAsync<MemberTenantDto>(TestContext.Current.CancellationToken))!;
        var bobTenant = (await bobResponse.Content.ReadFromJsonAsync<MemberTenantDto>(TestContext.Current.CancellationToken))!;
        Assert.NotEqual(aliceTenant.Id, bobTenant.Id);
        Assert.Equal(bob, bobTenant.Membership.SubjectId);

        // Bob's own key now names his own tenant: Alice's body is a different request of his, never her tenant.
        await ProvisioningApiTests.AssertProblemAsync(bobWithAlicesBody, HttpStatusCode.UnprocessableEntity, "idempotency-key-reused");
        Assert.DoesNotContain(aliceTenant.Id.ToString(), await bobWithAlicesBody.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AProvisioningKey_AndASelfServiceKey_AreIndependent()
    {
        fixture.RequireAvailable();
        var key = Guid.NewGuid().ToString("D");
        using (var admin = fixture.CreateClient(fixture.AdminToken()))
        {
            using var provisioned = await TenantMerchantApiFixture.ProvisionAsync(admin, ProvisioningApiTests.Body("key-provisioned"), key);
            Assert.Equal(HttpStatusCode.Created, provisioned.StatusCode);
        }

        using var selfService = await CreateAsync(fixture.CreateClient(fixture.UserToken(NewSubject())), "key-self-service", key: key);

        Assert.Equal(HttpStatusCode.Created, selfService.StatusCode);
    }

    [Fact]
    public async Task ASlugTakenByAProvisionedOrASelfServiceTenant_Is409()
    {
        fixture.RequireAvailable();
        await fixture.ProvisionTenantAsync("taken-provisioned", NewSubject());
        using (var other = await CreateAsync(fixture.CreateClient(fixture.UserToken(NewSubject())), "taken-self-service"))
        {
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        }

        using var client = fixture.CreateClient(fixture.UserToken(NewSubject()));
        using var provisionedSlug = await CreateAsync(client, "taken-provisioned");
        using var selfServiceSlug = await CreateAsync(client, "taken-self-service");

        await ProvisioningApiTests.AssertProblemAsync(provisionedSlug, HttpStatusCode.Conflict, "tenant-slug-taken");
        await ProvisioningApiTests.AssertProblemAsync(selfServiceSlug, HttpStatusCode.Conflict, "tenant-slug-taken");
    }

    [Fact]
    public async Task TheDefaultLimit_IsOneSelfServiceTenantPerSubject()
    {
        fixture.RequireAvailable();
        using var client = fixture.CreateClient(fixture.UserToken(NewSubject()));
        using var first = await CreateAsync(client, "limit-first");

        using var second = await CreateAsync(client, "limit-second");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await ProvisioningApiTests.AssertProblemAsync(second, HttpStatusCode.Conflict, "tenant-limit-reached");
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'limit-second'"));
    }

    [Fact]
    public async Task ProvisionedOwnershipsAndInvitedMemberships_DoNotCountAgainstTheLimit()
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        await fixture.ProvisionTenantAsync($"owned-{subject[^8..]}", subject);
        var other = await fixture.ProvisionTenantAsync($"joined-{subject[^8..]}", NewSubject());
        await fixture.SeedMemberAsync(other, subject, MerchantRole.MerchantAdmin);

        using var response = await CreateAsync(fixture.CreateClient(fixture.UserToken(subject)), $"own-{subject[^8..]}");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task AConfiguredLimit_AllowsThatManyTenants()
    {
        fixture.RequireAvailable();
        await using var factory = fixture.CreateFactory(
            fixture.Database.ConnectionString,
            builder => builder.UseSetting("TenantMerchant:Onboarding:MaxTenantsPerSubject", "2"));
        using var client = TenantMerchantApiFixture.CreateClient(factory, fixture.UserToken(NewSubject()));

        using var first = await CreateAsync(client, "configured-first");
        using var second = await CreateAsync(client, "configured-second");
        using var third = await CreateAsync(client, "configured-third");

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Conflict], [first.StatusCode, second.StatusCode, third.StatusCode]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    public async Task AnOutOfRangeLimit_StopsTheHost(string limit)
    {
        fixture.RequireAvailable();
        await using var factory = fixture.CreateFactory(
            fixture.Database.ConnectionString,
            builder => builder.UseSetting("TenantMerchant:Onboarding:MaxTenantsPerSubject", limit));

        var failure = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("TenantMerchant:Onboarding is invalid", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheAuditInsertFails_Is503AndLeavesNothing_AndARetrySucceeds()
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        var key = Guid.NewGuid().ToString("D");
        await fixture.Database.ExecuteAsync($"ALTER TABLE MembershipAuditRecords ADD CONSTRAINT CK_Test_RejectOnboardingAudit CHECK (ActorId <> N'{subject}')");
        try
        {
            using var failed = await CreateAsync(fixture.CreateClient(fixture.UserToken(subject)), "audit-failing", key: key);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.True(failed.Headers.Contains("Retry-After"));
            var body = await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("CK_Test_RejectOnboardingAudit", body, StringComparison.Ordinal);
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug = 'audit-failing'"));
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE SubjectId = N'{subject}'"));
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM MerchantProfiles WHERE DisplayName = N'audit-failing store'"));
            await fixture.AssertOneTenantCreatedPerTenantAsync();
        }
        finally
        {
            await fixture.Database.ExecuteAsync("ALTER TABLE MembershipAuditRecords DROP CONSTRAINT CK_Test_RejectOnboardingAudit");
        }

        using var retried = await CreateAsync(fixture.CreateClient(fixture.UserToken(subject)), "audit-failing", key: key);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        await fixture.AssertOneTenantCreatedPerTenantAsync();
    }

    [Fact]
    public async Task TheCreationIsLogged_WithoutTheRequestBody()
    {
        fixture.RequireAvailable();
        var subject = NewSubject();

        using var response = await CreateAsync(fixture.CreateClient(fixture.UserToken(subject)), "logged", displayName: "Secretive Display Name");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var events = fixture.Logs.Events.ToList();
        Assert.Contains(events, e => e.MessageTemplate.Text.StartsWith("Tenant {TenantId} created by self-service", StringComparison.Ordinal)
            && e.Properties["Subject"].ToString() == $"\"{subject}\"");
        Assert.DoesNotContain(fixture.Logs.AllText(), text => text.Contains("Secretive Display Name", StringComparison.Ordinal));
    }

    internal static string NewSubject() => $"user-{Guid.NewGuid():N}";

    /// <summary>Sends <c>POST /api/v1/tenants</c> with an idempotency key (a new one unless given).</summary>
    internal static async Task<HttpResponseMessage> CreateAsync(
        HttpClient client,
        string? slug = null,
        string? key = null,
        string? displayName = null,
        HttpContent? content = null,
        bool omitKey = false,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/tenants")
        {
            Content = content ?? JsonContent.Create(new { slug, displayName = displayName ?? $"{slug} store" }),
        };
        if (!omitKey)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key ?? Guid.NewGuid().ToString("D"));
        }

        configure?.Invoke(request);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
