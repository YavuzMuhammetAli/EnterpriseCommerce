using EnterpriseCommerce.Cart.Application.Carts;
using EnterpriseCommerce.Cart.Application.Common;
using EnterpriseCommerce.Cart.Domain.Carts;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseCommerce.Cart.IntegrationTests.Persistence;

/// <summary>The SQL Server store (ADR-029 section 8): persistence, constraints, optimistic concurrency and retries.</summary>
public sealed class PersistenceTests(SqlServerTestDatabase database) : IClassFixture<SqlServerTestDatabase>
{
    private static readonly Guid Merchant = Guid.CreateVersion7();

    [Fact]
    public async Task AFirstAdd_CreatesTheCartAndItsLine_WithARowVersionStamp()
    {
        database.RequireAvailable();
        var subject = NewSubject();
        var snapshot = TestSnapshots.Of(Merchant);

        var result = await AddAsync(subject, snapshot, 2);

        Assert.Equal(CartOutcome.Added, result.Result);
        var view = Assert.IsType<CartView>(result.Committed);
        Assert.Equal(1, view.Version);
        Assert.Matches("^[0-9A-F]{16}$", view.Stamp);
        Assert.Equal(Merchant, view.MerchantTenantId);
        var line = Assert.Single(view.Items);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(snapshot.UnitPrice.Amount, line.UnitPrice);
        Assert.Equal([new VariantOption("size", "M")], line.Options);

        var stored = await FindAsync(subject);
        Assert.Equal(view, stored, CartViewComparer.Instance);
    }

    [Fact]
    public async Task EveryWrite_IncreasesTheStamp_AndTheVersion()
    {
        database.RequireAvailable();
        var subject = NewSubject();
        var first = (await AddAsync(subject, TestSnapshots.Of(Merchant), 1)).Committed!;
        var second = (await AddAsync(subject, TestSnapshots.Of(Merchant), 1)).Committed!;

        Assert.Equal(2, second.Version);
        Assert.True(string.CompareOrdinal(second.Stamp, first.Stamp) > 0);

        // A line-only change still writes the cart row, so the stamp moves too.
        var third = (await database.Store().ExecuteAsync(subject, session =>
        {
            var outcome = session.Cart!.SetQuantity(session.Cart.Items[0].Id, 5, session.Now);
            return CartDecision<CartOutcome>.Commit(outcome);
        }, TestContext.Current.CancellationToken)).Committed!;
        Assert.True(string.CompareOrdinal(third.Stamp, second.Stamp) > 0);
    }

    [Fact]
    public async Task ADiscardedDecision_WritesNothing()
    {
        database.RequireAvailable();
        var subject = NewSubject();

        var result = await database.Store().ExecuteAsync(subject, session =>
        {
            var cart = session.StartCart();
            cart.AddItem(Guid.CreateVersion7(), TestSnapshots.Of(Merchant), 1, session.Now);
            return CartDecision<int>.Discard(0);
        }, TestContext.Current.CancellationToken);

        Assert.Null(result.Committed);
        Assert.Null(await FindAsync(subject));
    }

    [Fact]
    public async Task RemovingLines_DeletesTheirRows()
    {
        database.RequireAvailable();
        var subject = NewSubject();
        await AddAsync(subject, TestSnapshots.Of(Merchant), 1);
        await AddAsync(subject, TestSnapshots.Of(Merchant), 1);

        var cleared = await database.Store().ExecuteAsync(subject, session => CartDecision<CartOutcome>.Commit(session.Cart!.Clear(session.Now)), TestContext.Current.CancellationToken);

        Assert.Empty(cleared.Committed!.Items);
        Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM CartItems i JOIN Carts c ON c.Id = i.CartId WHERE c.CustomerSubject = N'{subject}'"));
        Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject = N'{subject}' AND MerchantTenantId IS NULL AND Currency IS NULL"));
    }

    [Fact]
    public async Task ARowTheDatabaseRefuses_RollsTheWholeSaveBack()
    {
        database.RequireAvailable();
        var subject = NewSubject();
        await AddAsync(subject, TestSnapshots.Of(Merchant), 1);

        // A defect that bypassed the domain: the CHECK constraint is the backstop, and the new line is not kept either.
        await Assert.ThrowsAsync<DataStoreUnavailableException>(() => database.Store().ExecuteAsync(subject, session =>
        {
            session.Cart!.AddItem(Guid.CreateVersion7(), TestSnapshots.Of(Merchant), 1, session.Now);
            typeof(CartItem).GetProperty(nameof(CartItem.Quantity))!.SetValue(session.Cart.Items[0], 0);
            return CartDecision<int>.Commit(0);
        }, TestContext.Current.CancellationToken));

        var stored = await FindAsync(subject);
        Assert.Equal(1, stored!.Version);
        Assert.Equal(1, Assert.Single(stored.Items).Quantity);
    }

    public static TheoryData<string, string> ConstraintViolations() => new()
    {
        { "quantity 0", "UPDATE CartItems SET Quantity = 0 WHERE CartId = @cart" },
        { "quantity 100", "UPDATE CartItems SET Quantity = 100 WHERE CartId = @cart" },
        { "price 0", "UPDATE CartItems SET UnitPrice = 0 WHERE CartId = @cart" },
        { "price over the maximum", "UPDATE CartItems SET UnitPrice = 1000000000.01 WHERE CartId = @cart" },
        { "lower-case currency", "UPDATE CartItems SET Currency = 'eur' WHERE CartId = @cart" },
        { "unknown availability", "UPDATE CartItems SET Availability = 'Reserved' WHERE CartId = @cart" },
        { "merchant without currency", "UPDATE Carts SET Currency = NULL WHERE Id = @cart" },
        { "version 0", "UPDATE Carts SET Version = 0 WHERE Id = @cart" },
        { "empty name", "UPDATE CartItems SET Name = N'' WHERE CartId = @cart" },
        { "a second line of the variant", "INSERT INTO CartItems (Id, CartId, ProductId, VariantId, TenantId, Quantity, Name, Sku, Options, UnitPrice, Currency, Availability, PriceCheckedAt, AddedAt, UpdatedAt) SELECT NEWID(), CartId, ProductId, VariantId, TenantId, 1, Name, Sku, Options, UnitPrice, Currency, Availability, PriceCheckedAt, AddedAt, UpdatedAt FROM CartItems WHERE CartId = @cart" },
        { "a second cart of the customer", "INSERT INTO Carts (Id, CustomerSubject, Version, CreatedAt, UpdatedAt) SELECT NEWID(), CustomerSubject, 1, CreatedAt, UpdatedAt FROM Carts WHERE Id = @cart" },
    };

    [Theory]
    [MemberData(nameof(ConstraintViolations))]
    public async Task TheDatabase_EnforcesTheCartRules(string rule, string statement)
    {
        database.RequireAvailable();
        var subject = NewSubject();
        var cart = (await AddAsync(subject, TestSnapshots.Of(Merchant), 1)).Committed!;

        var error = await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync($"DECLARE @cart uniqueidentifier = '{cart.Id}'; {statement}"));

        Assert.True(error.Number is 547 or 2601 or 2627, $"{rule}: unexpected error {error.Number}");
    }

    [Fact]
    public async Task TwoContexts_ThatChangeOneCart_CannotBothWin()
    {
        database.RequireAvailable();
        var subject = NewSubject();
        await AddAsync(subject, TestSnapshots.Of(Merchant), 1);
        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var a = await first.Carts.Include(c => c.Items).SingleAsync(c => c.CustomerSubject == subject, TestContext.Current.CancellationToken);
        var b = await second.Carts.Include(c => c.Items).SingleAsync(c => c.CustomerSubject == subject, TestContext.Current.CancellationToken);

        a.SetQuantity(a.Items[0].Id, 3, DateTimeOffset.UtcNow);
        b.SetQuantity(b.Items[0].Id, 4, DateTimeOffset.UtcNow);
        await first.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, Assert.Single((await FindAsync(subject))!.Items).Quantity);
    }

    [Fact]
    public async Task ADecisionThatLosesEveryRace_IsRefused_AfterThreeAttempts_AndSavesNothing()
    {
        database.RequireAvailable();
        var subject = NewSubject();
        var cart = (await AddAsync(subject, TestSnapshots.Of(Merchant), 1)).Committed!;
        var attempts = 0;

        await Assert.ThrowsAsync<CartConcurrencyException>(() => database.Store().ExecuteAsync(subject, session =>
        {
            attempts++;
            session.Cart!.SetQuantity(session.Cart.Items[0].Id, 9, session.Now);

            // Another writer commits between this attempt's read and its save, every time.
            using var connection = new SqlConnection(database.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"UPDATE Carts SET Version = Version + 1 WHERE Id = '{cart.Id}'";
            command.ExecuteNonQuery();
            return CartDecision<int>.Commit(0);
        }, TestContext.Current.CancellationToken));

        Assert.Equal(3, attempts);
        Assert.Equal(1, Assert.Single((await FindAsync(subject))!.Items).Quantity);
    }

    [Fact]
    public async Task ConcurrentAddsOfOneVariant_SumTheirQuantities_WithoutALostUpdate()
    {
        database.RequireAvailable();
        const int callers = 8;
        for (var round = 0; round < 10; round++)
        {
            var subject = NewSubject();
            var snapshot = TestSnapshots.Of(Merchant);
            await AddAsync(subject, snapshot, 1);

            var outcomes = await Concurrently(callers, _ => TryAddAsync(subject, snapshot, 2));

            var applied = outcomes.Count(outcome => outcome == "applied");
            var line = Assert.Single((await FindAsync(subject))!.Items);
            Assert.Equal(1 + (2 * applied), line.Quantity);
            Assert.All(outcomes, outcome => Assert.True(outcome is "applied" or "conflict", outcome));
            Assert.True(applied >= 1, "At least the first committer wins.");
        }
    }

    [Fact]
    public async Task ConcurrentFirstAdds_OfOneCustomer_CreateOneCart()
    {
        database.RequireAvailable();
        const int callers = 8;
        for (var round = 0; round < 10; round++)
        {
            var subject = NewSubject();

            var outcomes = await Concurrently(callers, _ => TryAddAsync(subject, TestSnapshots.Of(Merchant), 1));

            var applied = outcomes.Count(outcome => outcome == "applied");
            Assert.Equal(1, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject = N'{subject}'"));
            Assert.Equal(applied, (await FindAsync(subject))!.Items.Count);
            Assert.True(applied >= 1);
        }
    }

    [Fact]
    public async Task ConcurrentClearAndAdd_Serialize_InSomeOrder()
    {
        database.RequireAvailable();
        for (var round = 0; round < 15; round++)
        {
            var subject = NewSubject();
            await AddAsync(subject, TestSnapshots.Of(Merchant), 1);
            var added = TestSnapshots.Of(Merchant);

            var outcomes = await Concurrently(2, caller => caller == 0
                ? TryAsync(subject, session => session.Cart!.Clear(session.Now))
                : TryAddAsync(subject, added, 1));

            var items = (await FindAsync(subject))!.Items;
            Assert.All(outcomes, outcome => Assert.True(outcome is "applied" or "conflict", outcome));

            // Either the clear ran last (no line), or the add ran last (only the added line), never a mix.
            Assert.True(items.Count == 0 || (items.Count == 1 && items[0].VariantId == added.VariantId), $"Round {round}: {items.Count} lines");
        }
    }

    [Fact]
    public async Task DifferentCustomers_DoNotInterfere()
    {
        database.RequireAvailable();
        const int customers = 16;
        var subjects = Enumerable.Range(0, customers).Select(_ => NewSubject()).ToArray();

        var outcomes = await Concurrently(customers, caller => TryAddAsync(subjects[caller], TestSnapshots.Of(Merchant), caller + 1));

        Assert.All(outcomes, outcome => Assert.Equal("applied", outcome));
        for (var caller = 0; caller < customers; caller++)
        {
            Assert.Equal(caller + 1, Assert.Single((await FindAsync(subjects[caller]))!.Items).Quantity);
        }
    }

    [Fact]
    public async Task TheCleanup_DeletesExpiredCartsAndTheirLines_AndKeepsTheOthers()
    {
        database.RequireAvailable();
        var expired = NewSubject();
        var active = NewSubject();
        await AddAsync(expired, TestSnapshots.Of(Merchant), 1);
        await database.ExecuteAsync($"UPDATE Carts SET UpdatedAt = DATEADD(day, -31, SYSUTCDATETIME()) WHERE CustomerSubject = N'{expired}'");
        await AddAsync(active, TestSnapshots.Of(Merchant), 1);

        var deleted = await database.Cleanup().RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(deleted >= 1);
        Assert.Null(await FindAsync(expired));
        Assert.NotNull(await FindAsync(active));
        Assert.Equal(0, await database.ScalarAsync<int>("SELECT COUNT(*) FROM CartItems i WHERE NOT EXISTS (SELECT 1 FROM Carts c WHERE c.Id = i.CartId)"));
    }

    [Fact]
    public async Task TheCleanup_WorksInBatches_UntilNothingIsLeft()
    {
        database.RequireAvailable();
        var subjects = Enumerable.Range(0, 7).Select(_ => NewSubject()).ToList();
        foreach (var subject in subjects)
        {
            await AddAsync(subject, TestSnapshots.Of(Merchant), 1);
        }

        var list = string.Join(", ", subjects.Select(subject => $"N'{subject}'"));
        await database.ExecuteAsync($"UPDATE Carts SET UpdatedAt = DATEADD(day, -60, SYSUTCDATETIME()) WHERE CustomerSubject IN ({list})");

        await database.Cleanup(batchSize: 2).RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject IN ({list})"));
    }

    private static string NewSubject() => $"customer-{Guid.NewGuid():N}";

    private Task<CartWriteResult<CartOutcome>> AddAsync(string subject, ProductSnapshot snapshot, int quantity) =>
        database.Store().ExecuteAsync(subject, session =>
        {
            var cart = session.Cart ?? session.StartCart();
            var outcome = cart.AddItem(Guid.CreateVersion7(), snapshot, quantity, session.Now);
            return outcome is CartOutcome.Added or CartOutcome.Incremented ? CartDecision<CartOutcome>.Commit(outcome) : CartDecision<CartOutcome>.Discard(outcome);
        }, TestContext.Current.CancellationToken);

    private Task<string> TryAddAsync(string subject, ProductSnapshot snapshot, int quantity) =>
        TryAsync(subject, session => (session.Cart ?? session.StartCart()).AddItem(Guid.CreateVersion7(), snapshot, quantity, session.Now));

    private async Task<string> TryAsync(string subject, Func<ICartWriteSession, CartOutcome> command)
    {
        try
        {
            var result = await database.Store().ExecuteAsync(subject, session => CartDecision<CartOutcome>.Commit(command(session)), TestContext.Current.CancellationToken);
            return result.Result is CartOutcome.Added or CartOutcome.Incremented or CartOutcome.Changed or CartOutcome.Unchanged ? "applied" : result.Result.ToString();
        }
        catch (CartConcurrencyException)
        {
            return "conflict";
        }
    }

    private async Task<CartView?> FindAsync(string subject)
    {
        var queries = database.Queries(out var scope);
        using (scope)
        {
            return await queries.FindAsync(subject, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<string[]> Concurrently(int callers, Func<int, Task<string>> work)
    {
        using var start = new Barrier(callers);
        return await Task.WhenAll(Enumerable.Range(0, callers).Select(caller => Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(30));
            return await work(caller);
        })));
    }

    /// <summary>Compares two views by value, including their lines.</summary>
    private sealed class CartViewComparer : IEqualityComparer<CartView>
    {
        public static readonly CartViewComparer Instance = new();

        private static readonly CartLineView[] NoLines = [];
        private static readonly VariantOption[] NoOptions = [];

        public bool Equals(CartView? x, CartView? y) =>
            x is not null && y is not null
            && x with { Items = NoLines } == y with { Items = NoLines }
            && x.Items.Count == y.Items.Count
            && x.Items.Zip(y.Items).All(pair => pair.First with { Options = NoOptions } == pair.Second with { Options = NoOptions } && pair.First.Options.SequenceEqual(pair.Second.Options));

        public int GetHashCode(CartView obj) => obj.Id.GetHashCode();
    }
}
