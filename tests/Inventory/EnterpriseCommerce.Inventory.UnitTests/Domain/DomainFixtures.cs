using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;

namespace EnterpriseCommerce.Inventory.UnitTests.Domain;

internal static class DomainFixtures
{
    public const string Subject = "merchant-user-1";
    public const string Client = "svc-test-order";
    public const string Correlation = "corr-1";

    public static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid Tenant = Guid.CreateVersion7();

    public static IdempotencyRecord Key(string key = "key-1") => new(key, SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public static InventoryItem Item(int onHand, Guid? tenant = null)
    {
        var (item, error) = InventoryItem.Create(
            tenant ?? Tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), SkuSnapshot.TryCreate("SKU-" + Guid.NewGuid().ToString("N")[..8])!, onHand,
            Subject, Key(), Correlation, Now);
        Assert.Equal(StockChangeError.None, error);
        return item!;
    }

    public static Reservation Reserve(InventoryItem item, int quantity, TimeSpan? lifetime = null) =>
        Place([(item, quantity)], lifetime).Reservation!;

    public static PlacementResult Place(IReadOnlyList<(InventoryItem Item, int Quantity)> lines, TimeSpan? lifetime = null, string key = "res-1") =>
        Reservation.Place(
            Tenant,
            Client,
            Key(key),
            [.. lines.Select(line => new ReservationLineRequest(line.Item.VariantId, line.Quantity))],
            lines.ToDictionary(line => line.Item.VariantId, line => line.Item),
            "order-1",
            lifetime ?? TimeSpan.FromMinutes(15),
            Correlation,
            Now);

    public static IReadOnlyDictionary<Guid, InventoryItem> ById(params InventoryItem[] items) => items.ToDictionary(item => item.Id);
}
