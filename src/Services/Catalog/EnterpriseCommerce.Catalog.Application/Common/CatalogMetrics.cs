using System.Diagnostics.Metrics;

namespace EnterpriseCommerce.Catalog.Application.Common;

/// <summary>
/// The service's meters (no exporter is connected yet; see PROJECT_DECISIONS section 2). Tags are bounded enumerations
/// only: never a tenant, product, user or other unbounded value.
/// </summary>
public static class CatalogMetrics
{
    public const string MeterName = "EnterpriseCommerce.Catalog";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> MembershipChecks = Meter.CreateCounter<long>(
        "catalog.membership_checks", "{check}", "Merchant membership verifications, by outcome.");

    private static readonly Counter<long> StoreUnavailable = Meter.CreateCounter<long>(
        "catalog.store_unavailable", "{request}", "Requests answered 503 because MongoDB could not complete the operation.");

    /// <summary>One verification: <c>allowed</c>, <c>not_visible</c>, <c>forbidden</c> or <c>unavailable</c>.</summary>
    public static void RecordMembershipCheck(string outcome) =>
        MembershipChecks.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>One request refused because the store failed: <c>read</c> or <c>write</c>.</summary>
    public static void RecordStoreUnavailable(string operation) =>
        StoreUnavailable.Add(1, new KeyValuePair<string, object?>("operation", operation));
}
