using System.Net;
using EnterpriseCommerce.Catalog.Application.Authorization;
using EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Authorization;

/// <summary>
/// The Tenant &amp; Merchant client of ADR-025 section 2, through the host's real HTTP clients: the svc-catalog
/// assertion and token, the verdict, correlation propagation, the single retry after a 401, and fail-closed behaviour
/// for every other answer. Each test uses its own stub state, so the class runs its tests one at a time.
/// </summary>
[Collection(nameof(MembershipVerifierCollection))]
public sealed class MembershipVerifierTests(CatalogApiFixture fixture) : IClassFixture<CatalogApiFixture>, IDisposable
{
    private static readonly Guid TenantId = Guid.CreateVersion7();

    private IMembershipVerifier Verifier => fixture.Factory.Services.GetRequiredService<IMembershipVerifier>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose() => fixture.TenantMerchant.Heal();

    [Fact]
    public async Task AnActiveMember_IsVerifiedWithTheirRole_AndTheCorrelationIdTravels()
    {
        fixture.TenantMerchant.SetMember(TenantId, "owner-v1", "MerchantOwner");

        var verdict = await Verifier.VerifyAsync(TenantId, "owner-v1", "corr-123", Token);

        Assert.Equal(new MembershipVerdict(true, "MerchantOwner"), verdict);
        Assert.Equal("corr-123", fixture.TenantMerchant.LastCorrelationId);
    }

    [Fact]
    public async Task ANonMember_GetsTheNegativeVerdict_AndARevocationAppliesAtOnce()
    {
        Assert.Equal(MembershipVerdict.Inactive, await Verifier.VerifyAsync(TenantId, "stranger-v1", null, Token));

        fixture.TenantMerchant.SetMember(TenantId, "staff-v1", "MerchantStaff");
        Assert.True((await Verifier.VerifyAsync(TenantId, "staff-v1", null, Token)).Active);
        fixture.TenantMerchant.RemoveMember(TenantId, "staff-v1");
        Assert.False((await Verifier.VerifyAsync(TenantId, "staff-v1", null, Token)).Active);
    }

    [Fact]
    public async Task TheServiceToken_IsCached_ButNeverTheVerdict()
    {
        await using var factory = fixture.CreateFactory();
        var verifier = factory.Services.GetRequiredService<IMembershipVerifier>();
        var tokensBefore = fixture.TenantMerchant.TokenRequests;
        var callsBefore = fixture.TenantMerchant.VerificationCalls;

        for (var i = 0; i < 3; i++)
        {
            await verifier.VerifyAsync(TenantId, "anyone", null, Token);
        }

        Assert.Equal(1, fixture.TenantMerchant.TokenRequests - tokensBefore);
        Assert.Equal(3, fixture.TenantMerchant.VerificationCalls - callsBefore);
    }

    [Fact]
    public async Task ARejectedToken_IsRenewedOnce()
    {
        fixture.TenantMerchant.SetMember(TenantId, "admin-v1", "MerchantAdmin");
        fixture.TenantMerchant.RejectTokens = 1;

        Assert.True((await Verifier.VerifyAsync(TenantId, "admin-v1", null, Token)).Active);
    }

    [Fact]
    public async Task ATokenRejectedTwice_FailsClosed()
    {
        fixture.TenantMerchant.RejectTokens = 2;

        await Assert.ThrowsAsync<MembershipUnavailableException>(() => Verifier.VerifyAsync(TenantId, "anyone", null, Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task EveryOtherStatus_FailsClosed(HttpStatusCode status)
    {
        fixture.TenantMerchant.FailWith = status;

        await Assert.ThrowsAsync<MembershipUnavailableException>(() => Verifier.VerifyAsync(TenantId, "anyone", null, Token));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"active\":\"true\",\"role\":\"MerchantOwner\"}")]
    [InlineData("{\"active\":true}")]
    [InlineData("{\"active\":true,\"role\":null}")]
    [InlineData("{\"active\":true,\"role\":\"\"}")]
    [InlineData("{\"active\":true,\"role\":\"merchantOwner\"}")]
    [InlineData("{\"active\":true,\"role\":\"Merchant Owner\"}")]
    [InlineData("{\"active\":true,\"role\":\"MerchantOwnerWithAVeryLongRoleNameBeyondLimit\"}")]
    public async Task AMalformedVerdict_FailsClosed(string body)
    {
        fixture.TenantMerchant.RawAnswer = body;

        await Assert.ThrowsAsync<MembershipUnavailableException>(() => Verifier.VerifyAsync(TenantId, "anyone", null, Token));
    }

    [Fact]
    public async Task ARefusedClient_FailsClosed()
    {
        await using var factory = fixture.CreateFactory();
        fixture.TenantMerchant.RefuseTokens = true;

        await Assert.ThrowsAsync<MembershipUnavailableException>(() =>
            factory.Services.GetRequiredService<IMembershipVerifier>().VerifyAsync(TenantId, "anyone", null, Token));
    }

    [Fact]
    public async Task AHangingTenantMerchant_FailsClosedAfterTheTimeout()
    {
        await using var factory = fixture.CreateFactory(builder => builder.UseSetting("Catalog:MembershipVerification:Timeout", "00:00:00.500"));
        var verifier = factory.Services.GetRequiredService<IMembershipVerifier>();
        await verifier.VerifyAsync(TenantId, "anyone", null, Token);
        fixture.TenantMerchant.Delay = TimeSpan.FromSeconds(5);

        await Assert.ThrowsAsync<MembershipUnavailableException>(() => verifier.VerifyAsync(TenantId, "anyone", null, Token));
    }

    [Fact]
    public async Task WithoutConfiguration_DevelopmentFailsClosed()
    {
        await using var factory = fixture.CreateFactory(builder =>
        {
            builder.UseSetting("Catalog:MembershipVerification:BaseAddress", string.Empty);
            builder.UseSetting("Catalog:MembershipVerification:TokenEndpoint", string.Empty);
            builder.UseSetting("Catalog:MembershipVerification:PrivateKeyPath", string.Empty);
        });

        await Assert.ThrowsAsync<MembershipUnavailableException>(() =>
            factory.Services.GetRequiredService<IMembershipVerifier>().VerifyAsync(TenantId, "anyone", null, Token));
    }

    [Fact]
    public void LogsNeverContainTheServiceToken()
    {
        Assert.DoesNotContain(fixture.Logs.AllText(), text => text.Contains("stub-service-token-", StringComparison.Ordinal));
    }
}

[CollectionDefinition(nameof(MembershipVerifierCollection), DisableParallelization = true)]
public sealed class MembershipVerifierCollection;
