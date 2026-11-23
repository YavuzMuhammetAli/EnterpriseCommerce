using System.Diagnostics.Metrics;
using EnterpriseCommerce.Catalog.Application.Authorization;
using EnterpriseCommerce.Catalog.Application.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace EnterpriseCommerce.Catalog.UnitTests.Application;

[CollectionDefinition(nameof(CatalogMetricsCollection), DisableParallelization = true)]
public sealed class CatalogMetricsCollection;

/// <summary>
/// The merchant authorization of ADR-025 section 2: Tenant &amp; Merchant's verdict decides, the role table maps it to
/// Catalog permissions, unknown roles grant nothing, and no verdict means no access. Serial: it reads the meters.
/// </summary>
[Collection(nameof(CatalogMetricsCollection))]
public sealed class CatalogAccessAuthorizerTests
{
    private static readonly Guid TenantId = Guid.CreateVersion7();
    private static readonly MerchantCaller Caller = new("user-1", "corr-1");

    public static TheoryData<string, CatalogPermission, CatalogAccess> RoleMatrix() => new()
    {
        { "MerchantOwner", CatalogPermission.ViewCatalog, CatalogAccess.Allowed },
        { "MerchantOwner", CatalogPermission.EditProducts, CatalogAccess.Allowed },
        { "MerchantOwner", CatalogPermission.PublishProducts, CatalogAccess.Allowed },
        { "MerchantOwner", CatalogPermission.ManageBrands, CatalogAccess.Allowed },
        { "MerchantAdmin", CatalogPermission.ViewCatalog, CatalogAccess.Allowed },
        { "MerchantAdmin", CatalogPermission.EditProducts, CatalogAccess.Allowed },
        { "MerchantAdmin", CatalogPermission.PublishProducts, CatalogAccess.Allowed },
        { "MerchantAdmin", CatalogPermission.ManageBrands, CatalogAccess.Allowed },
        { "MerchantStaff", CatalogPermission.ViewCatalog, CatalogAccess.Allowed },
        { "MerchantStaff", CatalogPermission.EditProducts, CatalogAccess.Allowed },
        { "MerchantStaff", CatalogPermission.PublishProducts, CatalogAccess.Forbidden },
        { "MerchantStaff", CatalogPermission.ManageBrands, CatalogAccess.Forbidden },
        { "MerchantAuditor", CatalogPermission.ViewCatalog, CatalogAccess.Forbidden },
        { "merchantowner", CatalogPermission.ViewCatalog, CatalogAccess.Forbidden },
    };

    [Theory]
    [MemberData(nameof(RoleMatrix))]
    public async Task TheRoleTable_DecidesEveryPermission(string role, CatalogPermission permission, CatalogAccess expected)
    {
        var verifier = new ScriptedVerifier(new MembershipVerdict(true, role));

        Assert.Equal(expected, await Authorizer(verifier).AuthorizeAsync(Caller, TenantId, permission, TestContext.Current.CancellationToken));
        Assert.Equal([(TenantId, "user-1", "corr-1")], verifier.Calls);
    }

    [Fact]
    public async Task NoActiveMembership_IsNotVisible()
    {
        var access = await Authorizer(new ScriptedVerifier(MembershipVerdict.Inactive))
            .AuthorizeAsync(Caller, TenantId, CatalogPermission.ViewCatalog, TestContext.Current.CancellationToken);

        Assert.Equal(CatalogAccess.NotVisible, access);
    }

    [Fact]
    public async Task NoVerdict_PropagatesAndGrantsNothing()
    {
        await Assert.ThrowsAsync<MembershipUnavailableException>(() =>
            Authorizer(new ScriptedVerifier(null)).AuthorizeAsync(Caller, TenantId, CatalogPermission.ViewCatalog, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EveryOutcome_IsCounted_WithBoundedTagsOnly()
    {
        var measurements = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CatalogMetrics.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((instrument.Name, value, tags.ToArray()));
            }
        });
        listener.Start();

        var token = TestContext.Current.CancellationToken;
        await Authorizer(new ScriptedVerifier(new MembershipVerdict(true, "MerchantOwner"))).AuthorizeAsync(Caller, TenantId, CatalogPermission.ViewCatalog, token);
        await Authorizer(new ScriptedVerifier(new MembershipVerdict(true, "MerchantStaff"))).AuthorizeAsync(Caller, TenantId, CatalogPermission.ManageBrands, token);
        await Authorizer(new ScriptedVerifier(MembershipVerdict.Inactive)).AuthorizeAsync(Caller, TenantId, CatalogPermission.ViewCatalog, token);
        await Assert.ThrowsAsync<MembershipUnavailableException>(() =>
            Authorizer(new ScriptedVerifier(null)).AuthorizeAsync(Caller, TenantId, CatalogPermission.ViewCatalog, token));
        CatalogMetrics.RecordStoreUnavailable("read");

        var checks = measurements.Where(m => m.Name == "catalog.membership_checks").ToList();
        Assert.Equal(["allowed", "forbidden", "not_visible", "unavailable"], checks.Select(m => (string)m.Tags.Single().Value!).Order(StringComparer.Ordinal));
        Assert.All(measurements, m => Assert.All(m.Tags, tag => Assert.Contains(tag.Key, new[] { "outcome", "operation" })));
        Assert.Contains(measurements, m => m.Name == "catalog.store_unavailable" && (string)m.Tags.Single().Value! == "read");
    }

    private static CatalogAccessAuthorizer Authorizer(IMembershipVerifier verifier) =>
        new(verifier, NullLogger<CatalogAccessAuthorizer>.Instance);

    private sealed class ScriptedVerifier(MembershipVerdict? verdict) : IMembershipVerifier
    {
        public List<(Guid TenantId, string Subject, string? CorrelationId)> Calls { get; } = [];

        public Task<MembershipVerdict> VerifyAsync(Guid tenantId, string subjectId, string? correlationId, CancellationToken cancellationToken)
        {
            Calls.Add((tenantId, subjectId, correlationId));
            return verdict is null
                ? Task.FromException<MembershipVerdict>(new MembershipUnavailableException("scripted"))
                : Task.FromResult(verdict);
        }
    }
}
