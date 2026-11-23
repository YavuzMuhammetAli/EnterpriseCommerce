using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Application.Onboarding;
using EnterpriseCommerce.TenantMerchant.Application.Provisioning;
using EnterpriseCommerce.TenantMerchant.Domain.Audit;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Application;

public sealed class CreateTenantHandlerTests
{
    private const string Client = "bff-merchant";
    private const string Alice = "user-alice";
    private const string Bob = "user-bob";
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryOnboardingGate _gate = new();
    private readonly OnboardingSettings _settings = new();

    private CreateTenantHandler Handler() =>
        new(_gate, _settings, new FixedTimeProvider(Now), NullLogger<CreateTenantHandler>.Instance);

    private static CreateTenantCommand Command(string subject = Alice, string key = "key-1", string? slug = "acme", string? displayName = "Acme Store") =>
        new(subject, Client, key, slug, displayName, "corr-1");

    [Fact]
    public async Task ValidRequest_CreatesTheTenantAndMakesTheCallerItsOwner()
    {
        var result = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.Created, result.Outcome);
        var tenant = Assert.Single(_gate.Tenants);
        Assert.Equal("acme", tenant.Slug);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(TenantOrigin.SelfService, tenant.Origin);
        Assert.Equal(Alice, tenant.SelfServiceSubjectId);
        Assert.Equal(Client, tenant.ProvisionedByClientId);
        Assert.Equal("key-1", tenant.ProvisioningKey);
        Assert.Equal(Alice, tenant.ProvisionedByActor);
        Assert.Equal("Acme Store", tenant.MerchantProfile.DisplayName);
        Assert.Equal(Now, tenant.CreatedAt);

        var owner = Assert.Single(_gate.Memberships);
        Assert.Equal((tenant.Id, Alice, MerchantRole.MerchantOwner, MembershipStatus.Active), (owner.TenantId, owner.SubjectId, owner.Role, owner.Status));

        var audit = Assert.Single(_gate.AuditRecords);
        Assert.Equal(MembershipAuditAction.SelfServiceOwnerMembershipCreated, audit.Action);
        Assert.Equal((AuditActorKind.User, Alice), (audit.ActorKind, audit.ActorId));
        Assert.Equal((owner.Id, Alice, "corr-1"), (audit.MembershipId!.Value, audit.SubjectId, audit.CorrelationId));

        Assert.Equal(tenant.Id, result.Tenant!.Id);
        Assert.Equal(owner.Id, result.Membership!.Id);
    }

    [Fact]
    public async Task ACreatedTenant_StagesTheRequestsCorrelationAsRootMessageMetadata()
    {
        // ADR-023 section 4: the TenantCreated message is a root message, so its causation ID is the correlation ID.
        await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(new MessageMetadata("corr-1", "corr-1"), Assert.Single(_gate.Metadata));
    }

    [Fact]
    public async Task InvalidRequest_ReportsEveryFieldAndWritesNothing()
    {
        var result = await Handler().HandleAsync(Command(slug: "Not A Slug", displayName: "\u0001"), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.Invalid, result.Outcome);
        Assert.Equal(["displayName", "slug"], result.Errors!.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(_gate.Tenants);
        Assert.Equal(0, _gate.Attempts);
    }

    [Fact]
    public async Task MissingFields_AreInvalid()
    {
        var result = await Handler().HandleAsync(Command(slug: null, displayName: null), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.Invalid, result.Outcome);
        Assert.Equal(2, result.Errors!.Count);
    }

    [Fact]
    public async Task ACallerWithoutAValidSubject_IsAProgrammingError() =>
        await Assert.ThrowsAsync<ArgumentException>(() => Handler().HandleAsync(Command(subject: "has space"), TestContext.Current.CancellationToken));

    [Fact]
    public async Task SameKeyAndRequest_ReplaysTheTenantWithoutCreatingAnother()
    {
        var first = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        var replay = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Tenant!.Id, replay.Tenant!.Id);
        Assert.Equal(first.Membership!.Id, replay.Membership!.Id);
        Assert.Single(_gate.Tenants);
        Assert.Single(_gate.AuditRecords);
    }

    [Theory]
    [InlineData("other-slug", "Acme Store")]
    [InlineData("acme", "Another Name")]
    public async Task SameKeyWithADifferentRequest_IsAReusedKey(string slug, string displayName)
    {
        await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        var result = await Handler().HandleAsync(Command(slug: slug, displayName: displayName), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.IdempotencyKeyReused, result.Outcome);
        Assert.Single(_gate.Tenants);
    }

    [Fact]
    public async Task TheSameKeyOfTwoSubjects_NeverReplaysTheOtherSubjectsTenant()
    {
        var alice = await Handler().HandleAsync(Command(Alice, "shared-key", "alice-store"), TestContext.Current.CancellationToken);

        var bob = await Handler().HandleAsync(Command(Bob, "shared-key", "bob-store"), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.Created, bob.Outcome);
        Assert.NotEqual(alice.Tenant!.Id, bob.Tenant!.Id);
        Assert.Equal(Bob, bob.Membership!.SubjectId);
        Assert.Equal(2, _gate.Tenants.Count);
    }

    [Fact]
    public async Task TheSameRequestOfAnotherSubject_IsNotAReplay()
    {
        await Handler().HandleAsync(Command(Alice, "shared-key"), TestContext.Current.CancellationToken);

        // Bob sends Alice's exact body and key: he is answered from his own namespace, where the slug is simply taken.
        var bob = await Handler().HandleAsync(Command(Bob, "shared-key"), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.SlugTaken, bob.Outcome);
        Assert.Null(bob.Tenant);
    }

    [Fact]
    public async Task TheDefaultLimit_AllowsOneSelfServiceTenantPerSubject()
    {
        await Handler().HandleAsync(Command(key: "key-1", slug: "first"), TestContext.Current.CancellationToken);

        var second = await Handler().HandleAsync(Command(key: "key-2", slug: "second"), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.TenantLimitReached, second.Outcome);
        Assert.Single(_gate.Tenants);
    }

    [Fact]
    public async Task AtTheLimit_AReplayIsStillAnswered()
    {
        var first = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        var replay = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.Replayed, replay.Outcome);
        Assert.Equal(first.Tenant!.Id, replay.Tenant!.Id);
    }

    [Fact]
    public async Task AHigherLimit_AllowsThatManyTenants()
    {
        _settings.MaxTenantsPerSubject = 2;

        var first = await Handler().HandleAsync(Command(key: "key-1", slug: "first"), TestContext.Current.CancellationToken);
        var second = await Handler().HandleAsync(Command(key: "key-2", slug: "second"), TestContext.Current.CancellationToken);
        var third = await Handler().HandleAsync(Command(key: "key-3", slug: "third"), TestContext.Current.CancellationToken);

        Assert.Equal([CreateTenantOutcome.Created, CreateTenantOutcome.Created, CreateTenantOutcome.TenantLimitReached], [first.Outcome, second.Outcome, third.Outcome]);
    }

    [Fact]
    public async Task ProvisionedTenantsAndOtherSubjectsTenants_DoNotCountAgainstTheLimit()
    {
        Assert.True(SubjectId.TryCreate(Alice, out var alice));
        Assert.True(ActorReference.TryCreate("admin:1", out var actor));
        Assert.True(TenantSlug.TryCreate("provisioned", out var slug));
        Assert.True(MerchantDisplayName.TryCreate("Provisioned", out var name));
        var provisioned = Tenant.Provision(slug, name, new ProvisioningRecord("svc-admin", "k", new byte[32], actor), Now);
        _gate.Tenants.Add(provisioned);
        _gate.Memberships.Add(Membership.CreateOwner(provisioned.Id, alice, Now));
        await Handler().HandleAsync(Command(Bob, "bob-key", "bob-store"), TestContext.Current.CancellationToken);

        var result = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.Created, result.Outcome);
    }

    [Fact]
    public async Task ATakenSlug_IsRefused()
    {
        await Handler().HandleAsync(Command(Bob, "bob-key"), TestContext.Current.CancellationToken);

        var result = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.SlugTaken, result.Outcome);
        Assert.Single(_gate.Tenants);
    }

    [Fact]
    public async Task ASlugTakenByAnotherSubjectAtTheSameInstant_IsClassifiedAgainAsTaken()
    {
        var handler = Handler();
        _gate.BeforeCommit = () => _gate.Tenants.Add(SelfServiceTenant(Bob, "bob-key", "acme"));

        var result = await handler.HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.SlugTaken, result.Outcome);
        Assert.Equal(2, _gate.Attempts);
        Assert.Equal(Bob, Assert.Single(_gate.Tenants).SelfServiceSubjectId);
    }

    [Fact]
    public async Task AConflictThatPersists_IsReportedAsTakenAfterTheSecondPass_WithoutLooping()
    {
        _gate.ConflictOnEveryCommit = true;

        var result = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.SlugTaken, result.Outcome);
        Assert.Equal(2, _gate.Attempts);
        Assert.Empty(_gate.Tenants);
    }

    [Fact]
    public async Task AReplayForACreatorWhoIsNoLongerActive_DisclosesNothing()
    {
        await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);
        _gate.Memberships.Single().Suspend(activeOwnerCount: 2, Now);

        var result = await Handler().HandleAsync(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(CreateTenantOutcome.NotAccessible, result.Outcome);
        Assert.Null(result.Tenant);
    }

    [Fact]
    public void TheOnboardingHash_IsStable_AndCoversEveryField()
    {
        var baseline = Hash("acme", "Acme Store", Alice);

        Assert.Equal(32, baseline.Length);
        Assert.Equal(baseline, Hash("acme", "Acme Store", Alice));
        Assert.NotEqual(baseline, Hash("acme2", "Acme Store", Alice));
        Assert.NotEqual(baseline, Hash("acme", "Acme Store 2", Alice));
        Assert.NotEqual(baseline, Hash("acme", "Acme Store", Bob));
        Assert.Equal(Canonical("tenant-onboarding/v1", "acme", "Acme Store", Alice), baseline);
    }

    [Fact]
    public void TheProvisioningHash_IsUnchangedBySelfService_AndNeverEqualsAnOnboardingHash()
    {
        Assert.True(TenantSlug.TryCreate("acme", out var slug));
        Assert.True(MerchantDisplayName.TryCreate("Acme Store", out var name));
        Assert.True(SubjectId.TryCreate(Alice, out var owner));
        Assert.True(ActorReference.TryCreate(Alice, out var actor));

        var provisioning = ProvisioningRequestHash.Compute(slug, name, owner, actor);

        // The S1 encoding, written out independently: stored provisioning hashes must keep matching their retries.
        Assert.Equal(Canonical("tenant-provisioning/v1", "acme", "Acme Store", Alice, Alice), provisioning);
        Assert.NotEqual(provisioning, OnboardingRequestHash.Compute(slug, name, owner));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    public void OnboardingSettings_AreBounded(int maxTenants, bool valid) =>
        Assert.Equal(valid, !new OnboardingSettings { MaxTenantsPerSubject = maxTenants }.Validate().Any());

    [Fact]
    public void OnboardingSettings_DefaultToOneTenantPerSubject() => Assert.Equal(1, new OnboardingSettings().MaxTenantsPerSubject);

    private static byte[] Hash(string slugValue, string displayName, string subject)
    {
        Assert.True(TenantSlug.TryCreate(slugValue, out var slug));
        Assert.True(MerchantDisplayName.TryCreate(displayName, out var name));
        Assert.True(SubjectId.TryCreate(subject, out var subjectId));
        return OnboardingRequestHash.Compute(slug, name, subjectId);
    }

    private static byte[] Canonical(params string[] fields)
    {
        var bytes = new List<byte>();
        foreach (var field in fields)
        {
            var encoded = Encoding.UTF8.GetBytes(field);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
            bytes.AddRange(length);
            bytes.AddRange(encoded);
        }

        return SHA256.HashData([.. bytes]);
    }

    private static Tenant SelfServiceTenant(string subject, string key, string slugValue)
    {
        Assert.True(TenantSlug.TryCreate(slugValue, out var slug));
        Assert.True(MerchantDisplayName.TryCreate("Concurrent", out var name));
        Assert.True(SubjectId.TryCreate(subject, out var subjectId));
        return Tenant.CreateBySelfService(slug, name, new SelfServiceRecord(Client, key, new byte[32], subjectId), Now);
    }
}
