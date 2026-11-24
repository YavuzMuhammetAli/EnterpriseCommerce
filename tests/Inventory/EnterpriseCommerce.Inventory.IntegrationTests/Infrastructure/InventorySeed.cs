using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;

/// <summary>Creates and changes inventory through the real write gate, as the application does.</summary>
internal static class InventorySeed
{
    public const string Subject = "merchant-owner-1";
    public const string Correlation = "corr-seed";

    public static readonly MessageMetadata Metadata = MessageMetadata.ForRequest(Correlation)!;

    public static IdempotencyRecord Key(string? key = null)
    {
        key ??= Guid.NewGuid().ToString("N");
        return new IdempotencyRecord(key, SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public static InventoryItem NewItem(Guid tenantId, int onHand, Guid? variantId = null, DateTimeOffset? now = null)
    {
        var (item, error) = InventoryItem.Create(
            tenantId, Guid.CreateVersion7(), variantId ?? Guid.CreateVersion7(), SkuSnapshot.TryCreate($"SKU-{Guid.NewGuid():N}"[..20])!, onHand,
            Subject, Key(), Correlation, now ?? DateTimeOffset.UtcNow);
        Assert.Equal(StockChangeError.None, error);
        return item!;
    }

    /// <summary>Creates a committed item with its initial stock.</summary>
    public static async Task<InventoryItem> CreateItemAsync(SqlServerTestDatabase database, Guid tenantId, int onHand, Guid? variantId = null)
    {
        var item = NewItem(tenantId, onHand, variantId);
        await database.Gate().ExecuteAsync(
            "test-create-item",
            (session, _) =>
            {
                session.UseMessageMetadata(Metadata);
                session.Add(item);
                return Task.FromResult(WriteDecision.Commit(item.Id));
            },
            TestContext.Current.CancellationToken);
        return item;
    }

    /// <summary>Adjusts a committed item under its row lock.</summary>
    public static Task<StockChange> AdjustAsync(SqlServerTestDatabase database, Guid tenantId, Guid itemId, int delta, AdjustmentReason reason) =>
        database.Gate().ExecuteAsync(
            "test-adjust",
            async (session, cancellationToken) =>
            {
                session.UseMessageMetadata(Metadata);
                var item = await session.LockItemAsync(tenantId, itemId, cancellationToken) ?? throw new InvalidOperationException("No item.");
                var change = item.Adjust(delta, reason, Actor.User(Subject), Key(), Correlation, session.Now);
                return change.Succeeded ? WriteDecision.Commit(change) : WriteDecision.Rollback(change);
            },
            TestContext.Current.CancellationToken);
}
