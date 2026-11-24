using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;

namespace EnterpriseCommerce.Inventory.UnitTests.Domain;

/// <summary>Placement, the lifecycle and the expiry rules of ADR-026 sections 6 and 7.</summary>
public sealed class ReservationTests
{
    private static readonly Actor Owner = Actor.Service(DomainFixtures.Client);

    [Fact]
    public void Placing_ReservesEveryLine_AndRecordsTheCreation()
    {
        var first = DomainFixtures.Item(onHand: 5);
        var second = DomainFixtures.Item(onHand: 3);

        var result = DomainFixtures.Place([(first, 2), (second, 3)]);

        var reservation = Assert.IsType<Reservation>(result.Reservation);
        Assert.Equal((ReservationStatus.Active, 1L, DomainFixtures.Now + TimeSpan.FromMinutes(15), "order-1"), (reservation.Status, reservation.Version, reservation.ExpiresAt, reservation.Reference));
        Assert.Equal((5, 2, 3), (first.OnHand, first.Reserved, first.Available));
        Assert.Equal((3, 3, 0), (second.OnHand, second.Reserved, second.Available));
        Assert.Equal([.. new[] { first, second }.OrderBy(item => item.Id).Select(item => item.Id)], reservation.Lines.Select(line => line.InventoryItemId));
        var created = Assert.IsType<ReservationCreated>(Assert.Single(reservation.DomainEvents));
        Assert.Equal(2, created.Reservation.Lines.Count);
        var movement = first.PendingMovements[^1];
        Assert.Equal((MovementType.Reservation, 0, 2, (Guid?)reservation.Id, ActorKind.Service), (movement.Type, movement.OnHandDelta, movement.ReservedDelta, movement.ReservationId, movement.ActorKind));
    }

    [Fact]
    public void AMultiLineReservation_WithOneShortLine_ReservesNothing()
    {
        var plenty = DomainFixtures.Item(onHand: 10);
        var scarce = DomainFixtures.Item(onHand: 1);
        var versions = (plenty.Version, scarce.Version);

        var result = DomainFixtures.Place([(plenty, 4), (scarce, 2)]);

        Assert.Null(result.Reservation);
        Assert.Equal(PlacementError.InsufficientStock, result.Error);
        Assert.Equal([scarce.VariantId], result.ShortVariants);
        Assert.Equal((0, 0), (plenty.Reserved, scarce.Reserved));
        Assert.Equal(versions, (plenty.Version, scarce.Version));
    }

    [Fact]
    public void TheLastUnit_CanBeReservedOnce()
    {
        var item = DomainFixtures.Item(onHand: 1);

        Assert.NotNull(DomainFixtures.Place([(item, 1)], key: "first").Reservation);
        Assert.Equal(PlacementError.InsufficientStock, DomainFixtures.Place([(item, 1)], key: "second").Error);
        Assert.Equal((1, 1, 0), (item.OnHand, item.Reserved, item.Available));
    }

    [Fact]
    public void AVariantWithoutAnItem_OrOfAnotherTenant_FailsTheWholeRequest()
    {
        var item = DomainFixtures.Item(onHand: 5);
        var foreign = DomainFixtures.Item(onHand: 5, tenant: Guid.CreateVersion7());
        var unknown = Guid.CreateVersion7();

        var result = Reservation.Place(
            DomainFixtures.Tenant, DomainFixtures.Client, DomainFixtures.Key(),
            [new(item.VariantId, 1), new(foreign.VariantId, 1), new(unknown, 1)],
            new Dictionary<Guid, InventoryItem> { [item.VariantId] = item, [foreign.VariantId] = foreign },
            null, TimeSpan.FromMinutes(15), DomainFixtures.Correlation, DomainFixtures.Now);

        Assert.Equal(PlacementError.UnknownVariant, result.Error);
        Assert.Equal([foreign.VariantId, unknown], result.UnknownVariants);
        Assert.Equal((0, 0), (item.Reserved, foreign.Reserved));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(Quantities.MaxLineQuantity + 1)]
    public void ALineQuantityOutOfRange_IsRefused(int quantity)
    {
        Assert.Equal(PlacementError.InvalidLines, DomainFixtures.Place([(DomainFixtures.Item(onHand: 5), quantity)]).Error);
    }

    [Fact]
    public void NoLines_RepeatedVariants_AndTooManyLines_AreRefused()
    {
        var item = DomainFixtures.Item(onHand: 5);
        Assert.False(Reservation.AreValidLines([]));
        Assert.False(Reservation.AreValidLines([new(item.VariantId, 1), new(item.VariantId, 1)]));
        Assert.False(Reservation.AreValidLines([.. Enumerable.Range(0, Reservation.MaxLines + 1).Select(_ => new ReservationLineRequest(Guid.CreateVersion7(), 1))]));
        Assert.True(Reservation.AreValidLines([.. Enumerable.Range(0, Reservation.MaxLines).Select(_ => new ReservationLineRequest(Guid.CreateVersion7(), 1))]));
    }

    [Fact]
    public void Committing_ConsumesTheStock_AndKeepsAvailabilityUnchanged()
    {
        var item = DomainFixtures.Item(onHand: 10);
        var reservation = DomainFixtures.Reserve(item, 4);
        reservation.ClearDomainEvents();

        var result = reservation.Commit(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now.AddMinutes(1));

        Assert.Equal(new TransitionResult(TransitionOutcome.Changed, ReservationStatus.Committed), result);
        Assert.Equal((6, 0, 6), (item.OnHand, item.Reserved, item.Available));
        Assert.Equal((2L, (DateTimeOffset?)DomainFixtures.Now.AddMinutes(1)), (reservation.Version, reservation.CompletedAt));
        Assert.IsType<ReservationCommitted>(Assert.Single(reservation.DomainEvents));
        Assert.Equal((MovementType.ReservationCommit, -4, -4), (item.PendingMovements[^1].Type, item.PendingMovements[^1].OnHandDelta, item.PendingMovements[^1].ReservedDelta));
    }

    [Fact]
    public void Releasing_ReturnsTheStock_AndKeepsOnHandUnchanged()
    {
        var item = DomainFixtures.Item(onHand: 10);
        var reservation = DomainFixtures.Reserve(item, 4);

        var result = reservation.Release(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now);

        Assert.Equal(new TransitionResult(TransitionOutcome.Changed, ReservationStatus.Released), result);
        Assert.Equal((10, 0, 10), (item.OnHand, item.Reserved, item.Available));
        Assert.IsType<ReservationReleased>(reservation.DomainEvents[^1]);
    }

    [Fact]
    public void CommitAndRelease_AreIdempotent_AndChangeStockExactlyOnce()
    {
        var committedItem = DomainFixtures.Item(onHand: 10);
        var committed = DomainFixtures.Reserve(committedItem, 4);
        committed.Commit(DomainFixtures.ById(committedItem), Owner, DomainFixtures.Correlation, DomainFixtures.Now);
        var releasedItem = DomainFixtures.Item(onHand: 10);
        var released = DomainFixtures.Reserve(releasedItem, 4);
        released.Release(DomainFixtures.ById(releasedItem), Owner, DomainFixtures.Correlation, DomainFixtures.Now);
        var versions = (committedItem.Version, releasedItem.Version, committed.DomainEvents.Count, released.DomainEvents.Count);

        Assert.Equal(new TransitionResult(TransitionOutcome.Unchanged, ReservationStatus.Committed), committed.Commit(DomainFixtures.ById(committedItem), Owner, DomainFixtures.Correlation, DomainFixtures.Now));
        Assert.Equal(new TransitionResult(TransitionOutcome.Unchanged, ReservationStatus.Released), released.Release(DomainFixtures.ById(releasedItem), Owner, DomainFixtures.Correlation, DomainFixtures.Now));

        Assert.Equal(versions, (committedItem.Version, releasedItem.Version, committed.DomainEvents.Count, released.DomainEvents.Count));
        Assert.Equal((6, 0), (committedItem.OnHand, committedItem.Reserved));
        Assert.Equal((10, 0), (releasedItem.OnHand, releasedItem.Reserved));
    }

    [Fact]
    public void TerminalStates_AreNeverReopened()
    {
        var item = DomainFixtures.Item(onHand: 10);
        var committed = DomainFixtures.Reserve(item, 1);
        committed.Commit(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now);
        var released = DomainFixtures.Reserve(item, 1);
        released.Release(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now);
        var expired = DomainFixtures.Reserve(item, 1, TimeSpan.FromMinutes(1));
        expired.Expire(DomainFixtures.ById(item), DomainFixtures.Correlation, DomainFixtures.Now.AddMinutes(2));
        var before = (item.OnHand, item.Reserved, item.Version);

        Assert.Equal(new TransitionResult(TransitionOutcome.Conflict, ReservationStatus.Committed), committed.Release(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now));
        Assert.Equal(new TransitionResult(TransitionOutcome.Conflict, ReservationStatus.Released), released.Commit(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now));
        Assert.Equal(new TransitionResult(TransitionOutcome.Conflict, ReservationStatus.Expired), expired.Commit(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now));
        Assert.Equal(new TransitionResult(TransitionOutcome.Unchanged, ReservationStatus.Expired), expired.Release(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now));
        foreach (var terminal in new[] { committed, released, expired })
        {
            Assert.Equal(TransitionOutcome.Unchanged, terminal.Expire(DomainFixtures.ById(item), DomainFixtures.Correlation, DomainFixtures.Now.AddDays(1)).Outcome);
        }

        Assert.Equal(before, (item.OnHand, item.Reserved, item.Version));
    }

    [Fact]
    public void Expiry_ReturnsTheStock_OnlyOnceItIsDue()
    {
        var item = DomainFixtures.Item(onHand: 10);
        var reservation = DomainFixtures.Reserve(item, 6, TimeSpan.FromMinutes(15));

        Assert.Equal(TransitionOutcome.Unchanged, reservation.Expire(DomainFixtures.ById(item), DomainFixtures.Correlation, DomainFixtures.Now.AddMinutes(14)).Outcome);
        Assert.Equal(6, item.Reserved);

        var result = reservation.Expire(DomainFixtures.ById(item), DomainFixtures.Correlation, DomainFixtures.Now.AddMinutes(15));

        Assert.Equal(new TransitionResult(TransitionOutcome.Changed, ReservationStatus.Expired), result);
        Assert.Equal((10, 0), (item.OnHand, item.Reserved));
        Assert.IsType<ReservationExpired>(reservation.DomainEvents[^1]);
        var movement = item.PendingMovements[^1];
        Assert.Equal((MovementType.ReservationExpiry, ActorKind.System, "reservation-expiry"), (movement.Type, movement.ActorKind, movement.ActorId));
    }

    [Fact]
    public void AnOverdueReservation_IsNeverCommitted_ItExpiresInstead()
    {
        var item = DomainFixtures.Item(onHand: 10);
        var reservation = DomainFixtures.Reserve(item, 6, TimeSpan.FromMinutes(15));

        var result = reservation.Commit(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now.AddMinutes(16));

        Assert.Equal(new TransitionResult(TransitionOutcome.ExpiredNow, ReservationStatus.Expired), result);
        Assert.Equal((10, 0), (item.OnHand, item.Reserved));
        Assert.IsType<ReservationExpired>(reservation.DomainEvents[^1]);
    }

    [Fact]
    public void ReleasingAnOverdueReservation_ExpiresIt()
    {
        var item = DomainFixtures.Item(onHand: 10);
        var reservation = DomainFixtures.Reserve(item, 6, TimeSpan.FromMinutes(15));

        var result = reservation.Release(DomainFixtures.ById(item), Owner, DomainFixtures.Correlation, DomainFixtures.Now.AddMinutes(15));

        Assert.Equal(new TransitionResult(TransitionOutcome.ExpiredNow, ReservationStatus.Expired), result);
        Assert.Equal(MovementType.ReservationExpiry, item.PendingMovements[^1].Type);
    }

    [Fact]
    public void ATransition_WithoutTheLinesLockedItems_FailsClosed()
    {
        var item = DomainFixtures.Item(onHand: 10);
        var reservation = DomainFixtures.Reserve(item, 1);

        Assert.Throws<InvalidOperationException>(() => reservation.Commit(new Dictionary<Guid, InventoryItem>(), Owner, DomainFixtures.Correlation, DomainFixtures.Now));
        Assert.Equal(ReservationStatus.Active, reservation.Status);
    }
}
