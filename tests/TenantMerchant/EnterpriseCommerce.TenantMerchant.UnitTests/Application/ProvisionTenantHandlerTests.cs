using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Application;

public sealed class ProvisionTenantHandlerTests
{
    private const string Client = "svc-admin";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryTenantStore _store = new();
    private readonly ProvisionTenantHandler _handler;

    public ProvisionTenantHandlerTests() =>
        _handler = new ProvisionTenantHandler(_store, _store, new FixedTimeProvider(Now), NullLogger<ProvisionTenantHandler>.Instance);

    [Fact]
    public async Task ValidRequest_CreatesTheTenantProfileAndOwner()
    {
        var result = await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.Created, result.Outcome);
        var tenant = Assert.Single(_store.Tenants);
        Assert.Equal("acme", tenant.Slug);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal("Acme Store", tenant.MerchantProfile.DisplayName);
        Assert.Equal(Client, tenant.ProvisionedByClientId);
        Assert.Equal("key-1", tenant.ProvisioningKey);
        Assert.Equal("admin:7", tenant.ProvisionedByActor);
        Assert.Equal(32, tenant.ProvisioningRequestHash.Length);
        Assert.Equal(Now, tenant.CreatedAt);

        var owner = Assert.Single(_store.Memberships);
        Assert.Equal(tenant.Id, owner.TenantId);
        Assert.Equal("user-1", owner.SubjectId);
        Assert.Equal(MerchantRole.MerchantOwner, owner.Role);
        Assert.Equal(MembershipStatus.Active, owner.Status);

        Assert.Equal(tenant.Id, result.Tenant!.Id);
        Assert.Equal(owner.Id, result.Owner!.Id);
    }

    [Fact]
    public async Task InvalidRequest_ReportsEveryFieldAndWritesNothing()
    {
        var result = await _handler.HandleAsync(
            new ProvisionTenantCommand(Client, "key-1", "Not A Slug", " ", "has space", "admin 7"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.Invalid, result.Outcome);
        Assert.Equal(["actorReference", "displayName", "ownerSubjectId", "slug"], result.Errors!.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(_store.Tenants);
    }

    [Fact]
    public async Task MissingFields_AreInvalid()
    {
        var result = await _handler.HandleAsync(new ProvisionTenantCommand(Client, "key-1", null, null, null, null), TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.Invalid, result.Outcome);
        Assert.Equal(4, result.Errors!.Count);
    }

    [Fact]
    public async Task SameKeyAndSameRequest_IsReplayedWithoutASecondTenant()
    {
        var first = await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        // A retry may pad the display name differently; the validated request is the same.
        var retry = await _handler.HandleAsync(Command(displayName: " Acme Store "), TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.Replayed, retry.Outcome);
        Assert.Equal(first.Tenant!.Id, retry.Tenant!.Id);
        Assert.Equal(first.Owner!.Id, retry.Owner!.Id);
        Assert.Single(_store.Tenants);
        Assert.Equal(1, _store.Inserts);
    }

    [Theory]
    [InlineData("other", "Acme Store", "user-1", "admin:7")]
    [InlineData("acme", "Other Name", "user-1", "admin:7")]
    [InlineData("acme", "Acme Store", "user-2", "admin:7")]
    [InlineData("acme", "Acme Store", "user-1", "admin:8")]
    public async Task SameKeyWithADifferentRequest_IsRejected(string slug, string displayName, string owner, string actor)
    {
        await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        var result = await _handler.HandleAsync(Command(slug, displayName, owner, actor), TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.ProvisioningKeyReused, result.Outcome);
        Assert.Single(_store.Tenants);
    }

    [Fact]
    public async Task KeysAreScopedToTheCallingClient()
    {
        await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        var other = await _handler.HandleAsync(Command(slug: "globex") with { ClientId = "svc-other" }, TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.Created, other.Outcome);
        Assert.Equal(2, _store.Tenants.Count);
    }

    [Fact]
    public async Task ATakenSlug_IsAConflict()
    {
        await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        var result = await _handler.HandleAsync(Command(owner: "user-2") with { ProvisioningKey = "key-2" }, TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.SlugTaken, result.Outcome);
        Assert.Single(_store.Tenants);
    }

    [Fact]
    public async Task LosingARaceForTheSlug_IsAConflict()
    {
        _store.BeforeInsert = () =>
        {
            _store.BeforeInsert = null;
            _store.Tenants.Add(Tenant.Provision(Slug("acme"), Name("Rival"), new ProvisioningRecord(Client, "rival-key", new byte[32], Actor("admin:9")), Now));
        };

        var result = await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.SlugTaken, result.Outcome);
        Assert.Equal(0, _store.Inserts);
    }

    [Fact]
    public async Task LosingARaceToTheSameRequest_IsAReplay()
    {
        // The concurrent winner is the same request with the same key: its outcome is the one returned.
        var winner = new ProvisionTenantHandler(_store, _store, new FixedTimeProvider(Now), NullLogger<ProvisionTenantHandler>.Instance);
        _store.BeforeInsert = () =>
        {
            _store.BeforeInsert = null;
            winner.HandleAsync(Command(), CancellationToken.None).GetAwaiter().GetResult();
        };

        var result = await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.Replayed, result.Outcome);
        Assert.Single(_store.Tenants);
    }

    [Fact]
    public async Task ASameRequestCommittedBetweenTheKeyAndSlugLookups_IsAReplay()
    {
        // Found by the SQL Server concurrency test: the retry missed the key, then saw the slug of its own twin.
        var twin = new ProvisionTenantHandler(_store, _store, new FixedTimeProvider(Now), NullLogger<ProvisionTenantHandler>.Instance);
        _store.BeforeSlugCheck = () =>
        {
            _store.BeforeSlugCheck = null;
            twin.HandleAsync(Command(), CancellationToken.None).GetAwaiter().GetResult();
        };

        var result = await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(ProvisionTenantOutcome.Replayed, result.Outcome);
        Assert.Single(_store.Tenants);
    }

    [Fact]
    public void RequestHash_IsLengthPrefixed()
    {
        // Without length prefixes, ("ab", "c…") and ("abc", "…") could collide.
        var first = ProvisioningRequestHash.Compute(Slug("abc"), Name("def"), Subject("ghi"), Actor("jkl"));
        var second = ProvisioningRequestHash.Compute(Slug("abc"), Name("de"), Subject("fghi"), Actor("jkl"));

        Assert.NotEqual(first, second);
        Assert.Equal(first, ProvisioningRequestHash.Compute(Slug("abc"), Name("def"), Subject("ghi"), Actor("jkl")));
    }

    [Fact]
    public async Task ACreatedTenant_PassesTheRequestsCorrelationAsRootMessageMetadata()
    {
        await _handler.HandleAsync(Command() with { CorrelationId = "corr-7" }, TestContext.Current.CancellationToken);

        Assert.Equal(new MessageMetadata("corr-7", "corr-7"), Assert.Single(_store.Metadata));
    }

    [Fact]
    public async Task WithoutACorrelationId_NoMetadataIsPassed_SoTheSaveFailsClosed()
    {
        // The endpoints always supply one; the store refuses to write events without it (TenantMerchantDbContext).
        await _handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(_store.Metadata));
    }

    private static ProvisionTenantCommand Command(
        string slug = "acme",
        string displayName = "Acme Store",
        string owner = "user-1",
        string actor = "admin:7") =>
        new(Client, "key-1", slug, displayName, owner, actor);

    private static TenantSlug Slug(string value) => TenantSlug.TryCreate(value, out var slug) ? slug : throw new ArgumentException(value);

    private static MerchantDisplayName Name(string value) => MerchantDisplayName.TryCreate(value, out var name) ? name : throw new ArgumentException(value);

    private static SubjectId Subject(string value) => SubjectId.TryCreate(value, out var subject) ? subject : throw new ArgumentException(value);

    private static ActorReference Actor(string value) => ActorReference.TryCreate(value, out var actor) ? actor : throw new ArgumentException(value);
}
