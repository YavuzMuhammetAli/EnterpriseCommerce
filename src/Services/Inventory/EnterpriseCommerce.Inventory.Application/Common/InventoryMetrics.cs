using System.Diagnostics.Metrics;

namespace EnterpriseCommerce.Inventory.Application.Common;

/// <summary>
/// The service's business meters (<c>EnterpriseCommerce.Inventory</c>; INVENTORY_OPERATIONS.md section 4). Tags are
/// bounded closed sets: never a tenant, item, variant, SKU, reservation, subject or client. Exported once the
/// observability phase wires an exporter; tests observe them with a <see cref="MeterListener"/>.
/// </summary>
public static class InventoryMetrics
{
    public const string MeterName = "EnterpriseCommerce.Inventory";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> MembershipChecks = Meter.CreateCounter<long>("inventory.membership_checks", unit: "{check}");
    private static readonly Counter<long> CatalogVerifications = Meter.CreateCounter<long>("inventory.catalog_verifications", unit: "{verification}");
    private static readonly Counter<long> Adjustments = Meter.CreateCounter<long>("inventory.adjustments", unit: "{request}");
    private static readonly Counter<long> Reservations = Meter.CreateCounter<long>("inventory.reservations", unit: "{request}");
    private static readonly Counter<long> Transitions = Meter.CreateCounter<long>("inventory.reservation_transitions", unit: "{transition}");
    private static readonly Counter<long> Expired = Meter.CreateCounter<long>("inventory.reservations_expired", unit: "{reservation}");
    private static readonly Counter<long> ExpiryCycles = Meter.CreateCounter<long>("inventory.expiry_cycles", unit: "{cycle}");

    /// <summary><c>outcome</c>: <c>allowed</c>, <c>not_visible</c>, <c>forbidden</c> or <c>unavailable</c>.</summary>
    public static void RecordMembershipCheck(string outcome) => MembershipChecks.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary><c>outcome</c>: <c>eligible</c>, <c>not_found</c>, <c>not_eligible</c> or <c>unavailable</c>.</summary>
    public static void RecordCatalogVerification(string outcome) => CatalogVerifications.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary><c>outcome</c>: <c>applied</c>, <c>replayed</c> or <c>refused</c>.</summary>
    public static void RecordAdjustment(string outcome) => Adjustments.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary><c>outcome</c>: <c>created</c>, <c>replayed</c>, <c>insufficient_stock</c>, <c>unknown_variant</c> or <c>refused</c>.</summary>
    public static void RecordReservation(string outcome) => Reservations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary>
    /// <c>transition</c>: <c>commit</c> or <c>release</c>; <c>outcome</c>: <c>changed</c>, <c>unchanged</c>,
    /// <c>expired_now</c> or <c>conflict</c>.
    /// </summary>
    public static void RecordTransition(string transition, string outcome) =>
        Transitions.Add(1, new KeyValuePair<string, object?>("transition", transition), new KeyValuePair<string, object?>("outcome", outcome));

    /// <summary><c>by</c>: <c>worker</c> or <c>lazy</c>.</summary>
    public static void RecordExpired(string by) => Expired.Add(1, new KeyValuePair<string, object?>("by", by));

    /// <summary><c>outcome</c>: <c>completed</c> or <c>failed</c>.</summary>
    public static void RecordExpiryCycle(string outcome) => ExpiryCycles.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
