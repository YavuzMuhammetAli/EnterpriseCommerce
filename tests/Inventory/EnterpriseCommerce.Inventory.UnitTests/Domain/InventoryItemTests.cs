using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;

namespace EnterpriseCommerce.Inventory.UnitTests.Domain;

/// <summary>The balance invariants, the creation and the adjustment rules of ADR-026 sections 1 to 5.</summary>
public sealed class InventoryItemTests
{
    [Fact]
    public void ANewItem_HoldsItsInitialStock_AsVersionOne_WithAnInitialStockMovement()
    {
        var item = DomainFixtures.Item(onHand: 12);

        Assert.Equal((12, 0, 12, 1L), (item.OnHand, item.Reserved, item.Available, item.Version));
        var movement = Assert.Single(item.PendingMovements);
        Assert.Equal((MovementType.InitialStock, 12, 0, 0, 12, 1L), (movement.Type, movement.OnHandDelta, movement.OnHandBefore, movement.ReservedAfter, movement.OnHandAfter, movement.ItemVersion));
        Assert.Equal((ActorKind.User, DomainFixtures.Subject), (movement.ActorKind, movement.ActorId));
        var changed = Assert.IsType<StockLevelChanged>(Assert.Single(item.DomainEvents));
        Assert.Equal((movement.Id, 12, 12, 1L), (changed.MovementId, changed.OnHand, changed.Available, changed.ItemVersion));
    }

    [Fact]
    public void AnItem_MayStartWithNoStock()
    {
        Assert.Equal(0, DomainFixtures.Item(onHand: 0).OnHand);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(Quantities.MaxOnHand + 1)]
    public void AnInitialStockOutOfRange_IsRefused(int onHand)
    {
        var (item, error) = InventoryItem.Create(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), SkuSnapshot.TryCreate("SKU-1")!, onHand,
            DomainFixtures.Subject, DomainFixtures.Key(), DomainFixtures.Correlation, DomainFixtures.Now);

        Assert.Null(item);
        Assert.Equal(StockChangeError.QuantityOutOfRange, error);
    }

    [Theory]
    [InlineData(AdjustmentReason.Receipt, 5, 15)]
    [InlineData(AdjustmentReason.Return, 1, 11)]
    [InlineData(AdjustmentReason.Damage, -3, 7)]
    [InlineData(AdjustmentReason.Loss, -10, 0)]
    [InlineData(AdjustmentReason.Correction, -2, 8)]
    [InlineData(AdjustmentReason.Correction, 2, 12)]
    [InlineData(AdjustmentReason.CycleCount, -4, 6)]
    [InlineData(AdjustmentReason.CycleCount, 4, 14)]
    public void AnAdjustment_ChangesOnHandStock_AndRecordsItsMovement(AdjustmentReason reason, int delta, int expected)
    {
        var item = DomainFixtures.Item(onHand: 10);
        item.ClearPendingMovements();

        var change = item.Adjust(delta, reason, Actor.User(DomainFixtures.Subject), DomainFixtures.Key("adj-1"), DomainFixtures.Correlation, DomainFixtures.Now);

        Assert.True(change.Succeeded);
        Assert.Equal((expected, 0, 2L), (item.OnHand, item.Reserved, item.Version));
        var movement = Assert.Single(item.PendingMovements);
        Assert.Same(change.Movement, movement);
        Assert.Equal((MovementType.Adjustment, (AdjustmentReason?)reason, delta, 10, expected, "adj-1"), (movement.Type, movement.Reason, movement.OnHandDelta, movement.OnHandBefore, movement.OnHandAfter, movement.IdempotencyKey));
    }

    [Theory]
    [InlineData(AdjustmentReason.Receipt, -1)]
    [InlineData(AdjustmentReason.Return, -1)]
    [InlineData(AdjustmentReason.Damage, 1)]
    [InlineData(AdjustmentReason.Loss, 1)]
    public void AnAdjustmentAgainstItsReasonsSign_IsRefused(AdjustmentReason reason, int delta)
    {
        AssertRefused(DomainFixtures.Item(onHand: 10), delta, reason, StockChangeError.ReasonDoesNotAllowChange);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(Quantities.MaxAdjustment + 1)]
    [InlineData(-Quantities.MaxAdjustment - 1)]
    public void AnAdjustmentOutOfRange_IsRefused(int delta)
    {
        AssertRefused(DomainFixtures.Item(onHand: 10), delta, AdjustmentReason.Correction, StockChangeError.QuantityOutOfRange);
    }

    [Fact]
    public void AnUndefinedReason_IsRefused()
    {
        AssertRefused(DomainFixtures.Item(onHand: 10), 1, (AdjustmentReason)99, StockChangeError.ReasonDoesNotAllowChange);
    }

    [Fact]
    public void AnAdjustment_NeverLeavesOnHandBelowZero()
    {
        AssertRefused(DomainFixtures.Item(onHand: 3), -4, AdjustmentReason.Loss, StockChangeError.InsufficientUnreservedStock);
    }

    [Fact]
    public void AnAdjustment_NeverReducesOnHandBelowTheReservedQuantity()
    {
        var item = DomainFixtures.Item(onHand: 10);
        DomainFixtures.Reserve(item, 7);

        AssertRefused(item, -4, AdjustmentReason.Damage, StockChangeError.InsufficientUnreservedStock);

        var change = item.Adjust(-3, AdjustmentReason.Damage, Actor.User(DomainFixtures.Subject), DomainFixtures.Key(), DomainFixtures.Correlation, DomainFixtures.Now);
        Assert.True(change.Succeeded);
        Assert.Equal((7, 7, 0), (item.OnHand, item.Reserved, item.Available));
    }

    [Fact]
    public void AnAdjustment_NeverExceedsTheMaximum()
    {
        AssertRefused(DomainFixtures.Item(onHand: Quantities.MaxOnHand - 1), 2, AdjustmentReason.Receipt, StockChangeError.AboveMaximum);
    }

    [Fact]
    public void ReplayingTheMovements_ReproducesTheBalance()
    {
        var item = DomainFixtures.Item(onHand: 20);
        item.Adjust(5, AdjustmentReason.Receipt, Actor.User(DomainFixtures.Subject), DomainFixtures.Key("a"), DomainFixtures.Correlation, DomainFixtures.Now);
        var reservation = DomainFixtures.Reserve(item, 8);
        item.Adjust(-2, AdjustmentReason.Damage, Actor.User(DomainFixtures.Subject), DomainFixtures.Key("b"), DomainFixtures.Correlation, DomainFixtures.Now);
        reservation.Commit(DomainFixtures.ById(item), Actor.Service(DomainFixtures.Client), DomainFixtures.Correlation, DomainFixtures.Now);

        var movements = item.PendingMovements;
        Assert.Equal(Enumerable.Range(1, movements.Count).Select(version => (long)version), movements.Select(movement => movement.ItemVersion));
        Assert.All(movements, movement => Assert.Equal((movement.OnHandBefore + movement.OnHandDelta, movement.ReservedBefore + movement.ReservedDelta), (movement.OnHandAfter, movement.ReservedAfter)));
        Assert.Equal((item.OnHand, item.Reserved), (movements.Sum(movement => movement.OnHandDelta), movements.Sum(movement => movement.ReservedDelta)));
        Assert.Equal((15, 0, (long)movements.Count), (item.OnHand, item.Reserved, item.Version));
    }

    [Theory]
    [InlineData("SKU-1", true)]
    [InlineData("a.b_c-9", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("ümlaut", false)]
    public void ASkuSnapshot_FollowsCatalogsSkuRule(string value, bool valid)
    {
        Assert.Equal(valid, SkuSnapshot.TryCreate(value) is not null);
        Assert.Null(SkuSnapshot.TryCreate(new string('A', SkuSnapshot.MaxLength + 1)));
    }

    private static void AssertRefused(InventoryItem item, int delta, AdjustmentReason reason, StockChangeError expected)
    {
        var before = (item.OnHand, item.Reserved, item.Version, item.PendingMovements.Count, item.DomainEvents.Count);

        var change = item.Adjust(delta, reason, Actor.User(DomainFixtures.Subject), DomainFixtures.Key(), DomainFixtures.Correlation, DomainFixtures.Now);

        Assert.Equal(expected, change.Error);
        Assert.Null(change.Movement);
        Assert.Equal(before, (item.OnHand, item.Reserved, item.Version, item.PendingMovements.Count, item.DomainEvents.Count));
    }
}
