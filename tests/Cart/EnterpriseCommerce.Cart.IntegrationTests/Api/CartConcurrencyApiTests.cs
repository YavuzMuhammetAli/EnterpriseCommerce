using System.Net;
using EnterpriseCommerce.Cart.IntegrationTests.Infrastructure;

namespace EnterpriseCommerce.Cart.IntegrationTests.Api;

/// <summary>
/// Concurrent requests through the whole host (ADR-029 section 8): no lost update, no duplicate cart, no server error.
/// A request that keeps losing answers <c>409 concurrency-conflict</c> and changes nothing; every other one is applied.
/// </summary>
public sealed class CartConcurrencyApiTests(CartApiFixture fixture) : IClassFixture<CartApiFixture>
{
    [Fact]
    public async Task ConcurrentAddsOfOneVariant_AddUpExactly()
    {
        fixture.RequireAvailable();
        const int callers = 12;
        for (var round = 0; round < 5; round++)
        {
            var customer = CartApiFixture.NewCustomer();
            var variant = StubVariant.New();
            var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);

            var statuses = await Concurrently(callers, _ => StatusAsync(fixture.SendAsync(customer, HttpMethod.Post, "/api/v1/cart/items", new { productId = product, variantId = variant.Id, quantity = 1 })));

            Assert.All(statuses, status => Assert.True(status is HttpStatusCode.Created or HttpStatusCode.OK or HttpStatusCode.Conflict, status.ToString()));
            var applied = statuses.Count(status => status is HttpStatusCode.Created or HttpStatusCode.OK);
            Assert.Equal(1, statuses.Count(status => status == HttpStatusCode.Created));
            var cart = await fixture.GetCartAsync(customer);
            Assert.Equal(applied, Assert.Single(cart.GetProperty("items").EnumerateArray()).GetProperty("quantity").GetInt32());
            Assert.Equal(applied, cart.GetProperty("version").GetInt32());
            Assert.Equal(1, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Carts WHERE CustomerSubject = N'{customer}'"));
        }
    }

    [Fact]
    public async Task ConcurrentQuantityChanges_OfOneLine_LeaveOneOfTheRequestedValues()
    {
        fixture.RequireAvailable();
        var customer = CartApiFixture.NewCustomer();
        var variant = StubVariant.New();
        var cart = await fixture.AddAsync(customer, fixture.Catalog.Publish(Guid.CreateVersion7(), variant), variant.Id);
        var line = cart.GetProperty("items")[0].GetProperty("id").GetGuid();

        for (var round = 0; round < 5; round++)
        {
            var statuses = await Concurrently(8, caller => StatusAsync(fixture.SendAsync(customer, HttpMethod.Put, $"/api/v1/cart/items/{line}", new { quantity = 10 + caller })));

            Assert.All(statuses, status => Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Conflict, status.ToString()));
            var quantity = (await fixture.GetCartAsync(customer)).GetProperty("items")[0].GetProperty("quantity").GetInt32();
            Assert.InRange(quantity, 10, 17);
        }
    }

    [Fact]
    public async Task DifferentCustomers_AtOnce_AllSucceed()
    {
        fixture.RequireAvailable();
        const int customers = 16;
        var variant = StubVariant.New();
        var product = fixture.Catalog.Publish(Guid.CreateVersion7(), variant);
        var subjects = Enumerable.Range(0, customers).Select(_ => CartApiFixture.NewCustomer()).ToArray();

        var statuses = await Concurrently(customers, caller => StatusAsync(fixture.SendAsync(subjects[caller], HttpMethod.Post, "/api/v1/cart/items", new { productId = product, variantId = variant.Id, quantity = caller + 1 })));

        Assert.All(statuses, status => Assert.Equal(HttpStatusCode.Created, status));
        for (var caller = 0; caller < customers; caller++)
        {
            Assert.Equal(caller + 1, (await fixture.GetCartAsync(subjects[caller])).GetProperty("items")[0].GetProperty("quantity").GetInt32());
        }
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode[]> Concurrently(int callers, Func<int, Task<HttpStatusCode>> work)
    {
        using var start = new Barrier(callers);
        return await Task.WhenAll(Enumerable.Range(0, callers).Select(caller => Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(30));
            return await work(caller);
        })));
    }
}
