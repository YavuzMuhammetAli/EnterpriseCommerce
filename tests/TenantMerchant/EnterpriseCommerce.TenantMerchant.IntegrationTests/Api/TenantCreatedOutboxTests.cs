using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;
using EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Api;

/// <summary>
/// Both tenant creation paths write exactly one <c>TenantCreated</c> v1 outbox row in the creation's own transaction
/// (ADR-023 sections 5 and 9, PHASE_3_S4_EVENT_FLOW.md section 8). A replay or a refusal writes none, and a rolled-back
/// creation leaves none of its five rows.
/// </summary>
public sealed class TenantCreatedOutboxTests(TenantMerchantApiFixture fixture) : IClassFixture<TenantMerchantApiFixture>
{
    public enum CreationPath
    {
        Provisioning,
        SelfService,
    }

    public enum FailingTable
    {
        MembershipAuditRecords,
        OutboxMessages,
    }

    [Theory]
    [InlineData(CreationPath.Provisioning, "Provisioned")]
    [InlineData(CreationPath.SelfService, "SelfService")]
    public async Task ACreation_WritesOneSchemaValidTenantCreated_WithTheRequestsCorrelation(CreationPath path, string origin)
    {
        fixture.RequireAvailable();
        var slug = $"outbox-{origin.ToLowerInvariant()}";
        var correlation = $"corr-{Guid.NewGuid():N}";

        using var response = await CreateAsync(path, slug, NewSubject(), Guid.NewGuid().ToString("D"), correlation);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var tenantId = await TenantIdAsync(response);
        var row = Assert.Single(await OutboxRows.TenantCreatedAsync(fixture.Database, tenantId));
        Assert.Equal((OutboxRows.DefaultTopic, 1, 0), (row.Topic, row.EventVersion, row.Attempts));
        Assert.Null(row.PublishedAt);
        Assert.Equal(7, row.EventId.Version);
        Assert.Equal(await fixture.Database.ScalarAsync<DateTimeOffset>($"SELECT CreatedAt FROM Tenants WHERE Id = '{tenantId}'"), row.OccurredAt);

        EventSchemas.AssertValidTenantCreatedV1(row.Envelope);
        using var envelope = JsonDocument.Parse(row.Envelope);
        var root = envelope.RootElement;
        Assert.Equal(row.EventId.ToString("D"), root.GetProperty("eventId").GetString());
        Assert.Equal(tenantId.ToString("D"), root.GetProperty("tenantId").GetString());
        Assert.Equal((correlation, correlation), (root.GetProperty("correlationId").GetString(), root.GetProperty("causationId").GetString()));
        Assert.Equal((slug, $"{slug} store", "Active", origin), (
            root.GetProperty("payload").GetProperty("slug").GetString(),
            root.GetProperty("payload").GetProperty("displayName").GetString(),
            root.GetProperty("payload").GetProperty("status").GetString(),
            root.GetProperty("payload").GetProperty("origin").GetString()));
        await fixture.AssertOneTenantCreatedPerTenantAsync();
    }

    [Theory]
    [InlineData(CreationPath.Provisioning)]
    [InlineData(CreationPath.SelfService)]
    public async Task WithoutACorrelationHeader_TheGeneratedCorrelationId_IsTheEventsCorrelationAndCausation(CreationPath path)
    {
        fixture.RequireAvailable();

        using var response = await CreateAsync(path, $"generated-corr-{path.ToString().ToLowerInvariant()}", NewSubject(), Guid.NewGuid().ToString("D"), correlationId: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var generated = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));
        var row = Assert.Single(await OutboxRows.TenantCreatedAsync(fixture.Database, await TenantIdAsync(response)));
        using var envelope = JsonDocument.Parse(row.Envelope);
        Assert.Equal(generated, envelope.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal(generated, envelope.RootElement.GetProperty("causationId").GetString());
    }

    [Theory]
    [InlineData(CreationPath.Provisioning)]
    [InlineData(CreationPath.SelfService)]
    public async Task AReplay_WritesNoSecondEvent(CreationPath path)
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        var key = Guid.NewGuid().ToString("D");
        var slug = $"replayed-{path.ToString().ToLowerInvariant()}";

        using var first = await CreateAsync(path, slug, subject, key, "corr-first");
        using var replay = await CreateAsync(path, slug, subject, key, "corr-replay");

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, replay.StatusCode));
        var row = Assert.Single(await OutboxRows.TenantCreatedAsync(fixture.Database, await TenantIdAsync(first)));
        Assert.Contains("\"correlationId\":\"corr-first\"", row.Envelope, StringComparison.Ordinal);
        await fixture.AssertOneTenantCreatedPerTenantAsync();
    }

    [Fact]
    public async Task RefusedRequests_WriteNoEvent()
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        var key = Guid.NewGuid().ToString("D");
        using (var created = await CreateAsync(CreationPath.SelfService, "refusal-base", subject, key, "corr-base"))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var before = await CountTenantCreatedAsync();

        using var slugTakenByProvisioning = await CreateAsync(CreationPath.Provisioning, "refusal-base", NewSubject(), Guid.NewGuid().ToString("D"), "corr-1");
        using var slugTakenBySelfService = await CreateAsync(CreationPath.SelfService, "refusal-base", NewSubject(), Guid.NewGuid().ToString("D"), "corr-2");
        using var reusedKey = await CreateAsync(CreationPath.SelfService, "refusal-other", subject, key, "corr-3");
        using var limitReached = await CreateAsync(CreationPath.SelfService, "refusal-limit", subject, Guid.NewGuid().ToString("D"), "corr-4");
        using var invalid = await CreateAsync(CreationPath.Provisioning, "Not A Slug", NewSubject(), Guid.NewGuid().ToString("D"), "corr-5");

        Assert.Equal(
            [HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.UnprocessableEntity, HttpStatusCode.Conflict, HttpStatusCode.BadRequest],
            new[] { slugTakenByProvisioning, slugTakenBySelfService, reusedKey, limitReached, invalid }.Select(response => response.StatusCode));
        Assert.Equal(before, await CountTenantCreatedAsync());
        await fixture.AssertOneTenantCreatedPerTenantAsync();
    }

    [Theory]
    [InlineData(CreationPath.Provisioning, FailingTable.MembershipAuditRecords)]
    [InlineData(CreationPath.Provisioning, FailingTable.OutboxMessages)]
    [InlineData(CreationPath.SelfService, FailingTable.MembershipAuditRecords)]
    [InlineData(CreationPath.SelfService, FailingTable.OutboxMessages)]
    public async Task WhenAnyRowOfTheCreationFails_NoneOfTheFiveRowsRemains_AndARetryWritesExactlyOneEvent(CreationPath path, FailingTable table)
    {
        fixture.RequireAvailable();
        var subject = NewSubject();
        var key = Guid.NewGuid().ToString("D");
        var slug = $"rollback-{path.ToString().ToLowerInvariant()}-{table.ToString().ToLowerInvariant()}";
        var constraint = $"CK_Test_Reject_{path}_{table}";

        // Test-only failure injection: SQL Server itself rejects one row of the creation's transaction.
        var check = table == FailingTable.OutboxMessages
            ? $"Envelope NOT LIKE N'%\"slug\":\"{slug}\"%'"
            : $"SubjectId IS NULL OR SubjectId <> N'{subject}'";
        await fixture.Database.ExecuteAsync($"ALTER TABLE {table} ADD CONSTRAINT {constraint} CHECK ({check})");
        try
        {
            using var failed = await CreateAsync(path, slug, subject, key, "corr-rollback");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.True(failed.Headers.Contains("Retry-After"));
            Assert.DoesNotContain(constraint, await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Tenants WHERE Slug = '{slug}'"));
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM MerchantProfiles WHERE DisplayName = N'{slug} store'"));
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Memberships WHERE SubjectId = N'{subject}'"));
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM MembershipAuditRecords WHERE SubjectId = N'{subject}'"));
            Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE Envelope LIKE N'%\"slug\":\"{slug}\"%'"));
        }
        finally
        {
            await fixture.Database.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT {constraint}");
        }

        using var retried = await CreateAsync(path, slug, subject, key, "corr-retry");
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        var row = Assert.Single(await OutboxRows.TenantCreatedAsync(fixture.Database, await TenantIdAsync(retried)));
        Assert.Contains("\"correlationId\":\"corr-retry\"", row.Envelope, StringComparison.Ordinal);
        await fixture.AssertOneTenantCreatedPerTenantAsync();
    }

    [Theory]
    [InlineData(CreationPath.Provisioning)]
    [InlineData(CreationPath.SelfService)]
    public async Task ADisplayNameWithAnUnpairedSurrogate_IsRefusedWith400_AndWritesNothing(CreationPath path)
    {
        // PHASE_3_S4_EVENT_FLOW.md section 11 (to verify in step 3): an escaped lone surrogate in the request body.
        fixture.RequireAvailable();
        var subject = NewSubject();
        var before = await CountTenantCreatedAsync();
        var json = path == CreationPath.Provisioning
            ? $$"""{"slug":"lone-surrogate-p","displayName":"Bad \ud800 name","ownerSubjectId":"{{subject}}","actorReference":"admin:outbox"}"""
            : """{"slug":"lone-surrogate-s","displayName":"Bad \ud800 name"}""";

        using var response = await SendAsync(path, subject, Guid.NewGuid().ToString("D"), "corr-surrogate", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM Tenants WHERE Slug LIKE 'lone-surrogate-%'"));
        Assert.Equal(before, await CountTenantCreatedAsync());
    }

    private Task<int> CountTenantCreatedAsync() =>
        fixture.Database.ScalarAsync<int>("SELECT COUNT(*) FROM OutboxMessages WHERE EventType = 'TenantCreated'");

    private static string NewSubject() => $"user-{Guid.NewGuid():N}";

    private static async Task<Guid> TenantIdAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> CreateAsync(CreationPath path, string slug, string subject, string key, string? correlationId) =>
        SendAsync(path, subject, key, correlationId, JsonContent.Create(path == CreationPath.Provisioning
            ? new { slug, displayName = $"{slug} store", ownerSubjectId = subject, actorReference = "admin:outbox" }
            : (object)new { slug, displayName = $"{slug} store" }));

    private async Task<HttpResponseMessage> SendAsync(CreationPath path, string subject, string key, string? correlationId, HttpContent content)
    {
        using var client = fixture.CreateClient(path == CreationPath.Provisioning ? fixture.AdminToken() : fixture.UserToken(subject));
        using var request = new HttpRequestMessage(HttpMethod.Post, path == CreationPath.Provisioning ? "/internal/v1/tenants" : "/api/v1/tenants") { Content = content };
        request.Headers.Add("Idempotency-Key", key);
        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-ID", correlationId);
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
