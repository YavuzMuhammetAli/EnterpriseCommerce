using System.Net;
using System.Text.Json;
using EnterpriseCommerce.Inventory.IntegrationTests.Infrastructure;
using EnterpriseCommerce.Inventory.UnitTests.Infrastructure;

namespace EnterpriseCommerce.Inventory.IntegrationTests.Api;

/// <summary>
/// The internal reservation API on the real host and SQL Server (INVENTORY_API_CONTRACT.md section 7, ADR-026 sections 6
/// to 10): atomic placement, idempotency, the lifecycle with lazy expiry, owner isolation, and the concurrency rules
/// that prevent overselling.
/// </summary>
public sealed class ReservationApiTests(InventoryApiFixture fixture) : IClassFixture<InventoryApiFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AReservation_ReservesEveryLine_AndRecordsItsMovementsAndEvents()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var first = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var second = await fixture.CreateItemAsync(tenantId, owner, onHand: 3);

        using var response = await ReserveAsync(tenantId, [Line(first, 4), Line(second, 3)], reference: "order-42");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reservation = await InventoryApiFixture.ReadAsync(response);
        Assert.Equal(("Active", "order-42", 1L, 2), (Status(reservation), reservation.GetProperty("reference").GetString(), reservation.GetProperty("version").GetInt64(), reservation.GetProperty("lines").GetArrayLength()));
        Assert.Equal(TimeSpan.FromMinutes(15), reservation.GetProperty("expiresAt").GetDateTimeOffset() - reservation.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal((10, 4, 6), await BalanceAsync(first));
        Assert.Equal((3, 3, 0), await BalanceAsync(second));
        Assert.Equal(
            "ReservationCreated:1,StockLevelChanged:4",
            await fixture.Database.ScalarAsync<string>($"SELECT STRING_AGG(CONCAT(EventType, ':', n), ',') WITHIN GROUP (ORDER BY EventType) FROM (SELECT EventType, COUNT(*) AS n FROM OutboxMessages WHERE MessageKey = '{tenantId}' GROUP BY EventType) AS counts"));
        foreach (var envelope in await EnvelopesAsync(tenantId))
        {
            EventSchemas.AssertValid(envelope);
        }
    }

    [Fact]
    public async Task AMultiLineReservation_WithOneShortLine_ReservesNothing()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var plenty = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var scarce = await fixture.CreateItemAsync(tenantId, owner, onHand: 1);
        var events = await EventCountAsync(tenantId);

        using var response = await ReserveAsync(tenantId, [Line(plenty, 5), Line(scarce, 2)]);

        var problem = await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.Conflict, "insufficient-stock");
        Assert.Equal([scarce.GetProperty("variantId").GetGuid()], problem.GetProperty("variantIds").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Equal((10, 0, 10), await BalanceAsync(plenty));
        Assert.Equal((1, 0, 1), await BalanceAsync(scarce));
        Assert.Equal(events, await EventCountAsync(tenantId));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM Reservations WHERE TenantId = '{tenantId}'"));
    }

    [Fact]
    public async Task AVariantTheTenantDoesNotTrack_FailsTheWholeRequest_AndAnotherTenantsStockIsNeverReserved()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var otherTenant = fixture.NewTenant(out var otherOwner, out _, out _);
        var own = await fixture.CreateItemAsync(tenantId, owner, onHand: 5);
        var foreign = await fixture.CreateItemAsync(otherTenant, otherOwner, onHand: 5);

        using var response = await ReserveAsync(tenantId, [Line(own, 1), Line(foreign, 1)]);

        var problem = await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "inventory-item-not-found");
        Assert.Equal([foreign.GetProperty("variantId").GetGuid()], problem.GetProperty("variantIds").EnumerateArray().Select(id => id.GetGuid()));
        Assert.Equal((5, 0, 5), await BalanceAsync(own));
        Assert.Equal((5, 0, 5), await BalanceAsync(foreign));
    }

    [Fact]
    public async Task AReservation_IsIdempotentPerClientTenantAndKey()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var key = Guid.NewGuid().ToString("D");

        using var first = await ReserveAsync(tenantId, [Line(item, 2)], key: key);
        using var replay = await ReserveAsync(tenantId, [Line(item, 2)], key: key);
        using var reused = await ReserveAsync(tenantId, [Line(item, 3)], key: key);
        using var otherClient = await ReserveAsync(tenantId, [Line(item, 2)], key: key, client: TestTokenIssuer.OtherReservationClient);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Created), (first.StatusCode, replay.StatusCode, otherClient.StatusCode));
        var original = Id(await InventoryApiFixture.ReadAsync(first));
        Assert.Equal(original, Id(await InventoryApiFixture.ReadAsync(replay)));
        Assert.NotEqual(original, Id(await InventoryApiFixture.ReadAsync(otherClient)));
        await InventoryApiFixture.AssertProblemAsync(reused, HttpStatusCode.UnprocessableEntity, "idempotency-key-reused");
        Assert.Equal((10, 4, 6), await BalanceAsync(item));
    }

    [Fact]
    public async Task AReplay_AnswersTheOriginalReservation_InItsCurrentState()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var key = Guid.NewGuid().ToString("D");
        using var first = await ReserveAsync(tenantId, [Line(item, 2)], key: key);
        var id = Id(await InventoryApiFixture.ReadAsync(first));
        using (await TransitionAsync(tenantId, id, "commit"))
        {
        }

        using var replay = await ReserveAsync(tenantId, [Line(item, 2)], key: key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("Committed", Status(await InventoryApiFixture.ReadAsync(replay)));
        Assert.Equal((8, 0, 8), await BalanceAsync(item));
    }

    [Fact]
    public async Task ARequestedLineOrder_DoesNotChangeTheRequest()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var first = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var second = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var key = Guid.NewGuid().ToString("D");

        using var created = await ReserveAsync(tenantId, [Line(first, 1), Line(second, 2)], key: key);
        using var reordered = await ReserveAsync(tenantId, [Line(second, 2), Line(first, 1)], key: key);

        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (created.StatusCode, reordered.StatusCode));
    }

    [Fact]
    public async Task Committing_ConsumesTheStock_OnceOnly_AndACommittedReservationCannotBeReleased()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var id = await ReserveIdAsync(tenantId, [Line(item, 4)]);

        using var commit = await TransitionAsync(tenantId, id, "commit");
        using var again = await TransitionAsync(tenantId, id, "commit");
        using var release = await TransitionAsync(tenantId, id, "release");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (commit.StatusCode, again.StatusCode));
        var committed = await InventoryApiFixture.ReadAsync(again);
        Assert.Equal(("Committed", 2L), (Status(committed), committed.GetProperty("version").GetInt64()));
        await InventoryApiFixture.AssertProblemAsync(release, HttpStatusCode.Conflict, "reservation-committed");
        Assert.Equal((6, 0, 6), await BalanceAsync(item));
        Assert.Equal(1, await EventCountAsync(tenantId, "ReservationCommitted"));
    }

    [Fact]
    public async Task Releasing_ReturnsTheStock_OnceOnly_AndAReleasedReservationCannotBeCommitted()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var id = await ReserveIdAsync(tenantId, [Line(item, 4)]);

        using var release = await TransitionAsync(tenantId, id, "release");
        using var again = await TransitionAsync(tenantId, id, "release");
        using var commit = await TransitionAsync(tenantId, id, "commit");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (release.StatusCode, again.StatusCode));
        Assert.Equal("Released", Status(await InventoryApiFixture.ReadAsync(again)));
        await InventoryApiFixture.AssertProblemAsync(commit, HttpStatusCode.Conflict, "reservation-released");
        Assert.Equal((10, 0, 10), await BalanceAsync(item));
        Assert.Equal(1, await EventCountAsync(tenantId, "ReservationReleased"));
    }

    [Fact]
    public async Task AnOverdueReservation_IsNeverCommitted_ItExpiresInTheRequest()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var committing = await ReserveIdAsync(tenantId, [Line(item, 3)]);
        var releasing = await ReserveIdAsync(tenantId, [Line(item, 2)]);
        await MakeOverdueAsync(committing);
        await MakeOverdueAsync(releasing);

        using var commit = await TransitionAsync(tenantId, committing, "commit");
        using var release = await TransitionAsync(tenantId, releasing, "release");

        await InventoryApiFixture.AssertProblemAsync(commit, HttpStatusCode.Conflict, "reservation-expired");
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Equal("Expired", Status(await InventoryApiFixture.ReadAsync(release)));
        Assert.Equal((10, 0, 10), await BalanceAsync(item));
        Assert.Equal(("Expired", "Expired"), (await StatusOfAsync(committing), await StatusOfAsync(releasing)));
        Assert.Equal(2, await EventCountAsync(tenantId, "ReservationExpired"));
        Assert.Equal(2, await fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM StockMovements WHERE TenantId = '{tenantId}' AND Type = 'ReservationExpiry' AND ActorId = 'reservation-expiry'"));
    }

    [Fact]
    public async Task AReservation_IsVisibleOnlyToItsOwner_InItsTenant()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
        var id = await ReserveIdAsync(tenantId, [Line(item, 1)]);
        var other = fixture.ServiceToken(TestTokenIssuer.OtherReservationClient);

        using var read = await fixture.SendAsync(other, HttpMethod.Get, Reservations(tenantId) + $"/{id}");
        using var commit = await fixture.SendAsync(other, HttpMethod.Post, Reservations(tenantId) + $"/{id}/commit");
        using var release = await fixture.SendAsync(other, HttpMethod.Post, Reservations(tenantId) + $"/{id}/release");
        using var wrongTenant = await fixture.SendAsync(fixture.ServiceToken(), HttpMethod.Get, Reservations(Guid.CreateVersion7()) + $"/{id}");
        using var ownRead = await fixture.SendAsync(fixture.ServiceToken(), HttpMethod.Get, Reservations(tenantId) + $"/{id}");

        foreach (var response in new[] { read, commit, release, wrongTenant })
        {
            await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.NotFound, "reservation-not-found");
        }

        Assert.Equal(("Active", (10, 1, 9)), (Status(await InventoryApiFixture.ReadAsync(ownRead)), await BalanceAsync(item)));
    }

    public static TheoryData<string, TokenSpec> CallersRefusedByThePolicy() => new()
    {
        { "a client that is not allow-listed", new TokenSpec { Subject = "svc-rogue", ClientId = "svc-rogue" } },
        { "an allowed client without the scope", new TokenSpec { Scope = "svc.inventory.other" } },
        { "sub differs from client_id", new TokenSpec { Subject = "someone" } },
        { "a user principal type", new TokenSpec { PrincipalType = "user" } },
        { "a merchant user", TokenSpec.User("owner") },
    };

    [Theory]
    [MemberData(nameof(CallersRefusedByThePolicy))]
    public async Task OnlyAllowListedServiceClients_MayReserve(string caller, TokenSpec spec)
    {
        _ = caller;
        using var response = await fixture.SendAsync(fixture.Tokens.CreateToken(spec), HttpMethod.Post, Reservations(Guid.CreateVersion7()), new { lines = new[] { new { variantId = Guid.CreateVersion7(), quantity = 1 } } }, InventoryApiFixture.Key());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AServiceTokenForAnotherAudience_IsUnauthorized()
    {
        using var response = await fixture.SendAsync(fixture.Tokens.CreateToken(new TokenSpec { Audiences = ["catalog-api"] }), HttpMethod.Get, Reservations(Guid.CreateVersion7()) + $"/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string, string> InvalidRequests()
    {
        var variant = Guid.CreateVersion7();
        var data = new TheoryData<string, string>
        {
            { """{"lines":[]}""", "lines" },
            { $$"""{"lines":[{"variantId":"{{variant}}","quantity":1},{"variantId":"{{variant}}","quantity":2}]}""", "lines" },
            { $$"""{"lines":[{"variantId":"{{variant}}","quantity":0}]}""", "lines" },
            { $$"""{"lines":[{"variantId":"{{variant}}","quantity":1000001}]}""", "lines" },
            { $$"""{"lines":[{"variantId":"{{variant}}"}]}""", "lines" },
            { $$"""{"lines":[{"variantId":"{{variant}}","quantity":1}],"reference":"has space"}""", "reference" },
            { $$"""{"lines":[{"variantId":"{{variant}}","quantity":1}],"expiresInSeconds":59}""", "expiresInSeconds" },
            { $$"""{"lines":[{"variantId":"{{variant}}","quantity":1}],"expiresInSeconds":3601}""", "expiresInSeconds" },
        };
        data.Add($$"""{"lines":[{{string.Join(",", Enumerable.Range(0, 51).Select(_ => $$"""{"variantId":"{{Guid.CreateVersion7()}}","quantity":1}"""))}}]}""", "lines");
        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task AnInvalidRequest_IsRefused(string body, string field)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await fixture.SendAsync(fixture.ServiceToken(), HttpMethod.Post, Reservations(Guid.CreateVersion7()), content, InventoryApiFixture.Key());

        var problem = await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.GetRawText());
    }

    [Theory]
    [InlineData("""{"lines":[{"variantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","quantity":1.5}]}""")]
    [InlineData("""{"lines":[{"variantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","quantity":1,"price":10}]}""")]
    [InlineData("""{"lines":[{"variantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d","quantity":1}],"tenantId":"019ab9a2-bbbb-7ccc-8ddd-1e2f3a4b5c6d"}""")]
    public async Task AFractionalQuantity_OrAnUnknownMember_IsMalformed(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var response = await fixture.SendAsync(fixture.ServiceToken(), HttpMethod.Post, Reservations(Guid.CreateVersion7()), content, InventoryApiFixture.Key());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AReservationWithoutAnIdempotencyKey_IsRefused()
    {
        using var response = await fixture.SendAsync(fixture.ServiceToken(), HttpMethod.Post, Reservations(Guid.CreateVersion7()), new { lines = new[] { new { variantId = Guid.CreateVersion7(), quantity = 1 } } });

        var problem = await InventoryApiFixture.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("Idempotency-Key", out _));
    }

    [Fact]
    public async Task TheLastUnit_ConcurrentlyRequestedSixteenTimes_IsReservedExactlyOnce()
    {
        // The critical concurrency test: whatever the interleaving, the locked read of the item lets exactly one
        // transaction see the unit as available. Repeated to expose a race.
        fixture.RequireAvailable();
        const int Callers = 16;
        const int Rounds = 25;
        for (var round = 0; round < Rounds; round++)
        {
            var tenantId = fixture.NewTenant(out var owner, out _, out _);
            var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 1);

            var statuses = await ConcurrentlyAsync(Callers, _ => ReserveStatusAsync(tenantId, [Line(item, 1)]));

            Assert.Single(statuses, status => status == HttpStatusCode.Created);
            Assert.All(statuses.Where(status => status != HttpStatusCode.Created), status => Assert.Equal(HttpStatusCode.Conflict, status));
            Assert.Equal((1, 1, 0), await BalanceAsync(item));
            Assert.Equal(1, await EventCountAsync(tenantId, "ReservationCreated"));
        }
    }

    [Fact]
    public async Task ConcurrentReservations_NeverOversell_AndEveryUnitIsAccountedFor()
    {
        fixture.RequireAvailable();
        const int Callers = 20;
        for (var round = 0; round < 5; round++)
        {
            var tenantId = fixture.NewTenant(out var owner, out _, out _);
            var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 7);

            var statuses = await ConcurrentlyAsync(Callers, _ => ReserveStatusAsync(tenantId, [Line(item, 1)]));

            Assert.Equal(7, statuses.Count(status => status == HttpStatusCode.Created));
            Assert.Equal((7, 7, 0), await BalanceAsync(item));
            await AssertLedgerMatchesAsync(item);
        }
    }

    [Fact]
    public async Task OverlappingMultiLineReservations_InOppositeLineOrder_DoNotDeadlockOrOversell()
    {
        // The lock order is the item ID, never the request's line order (ADR-026 section 9).
        fixture.RequireAvailable();
        for (var round = 0; round < 10; round++)
        {
            var tenantId = fixture.NewTenant(out var owner, out _, out _);
            var a = await fixture.CreateItemAsync(tenantId, owner, onHand: 1);
            var b = await fixture.CreateItemAsync(tenantId, owner, onHand: 1);

            var statuses = await ConcurrentlyAsync(8, caller => ReserveStatusAsync(tenantId, caller % 2 == 0 ? [Line(a, 1), Line(b, 1)] : [Line(b, 1), Line(a, 1)]));

            Assert.Single(statuses, status => status == HttpStatusCode.Created);
            Assert.All(statuses.Where(status => status != HttpStatusCode.Created), status => Assert.Equal(HttpStatusCode.Conflict, status));
            Assert.Equal(((1, 1, 0), (1, 1, 0)), (await BalanceAsync(a), await BalanceAsync(b)));
        }
    }

    [Fact]
    public async Task ConcurrentRetriesOfOneReservation_CreateItOnce()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 3);
        var key = Guid.NewGuid().ToString("D");

        var outcomes = await ConcurrentlyAsync(8, async _ =>
        {
            using var response = await ReserveAsync(tenantId, [Line(item, 2)], key: key);
            return (response.StatusCode, Id: Id(await InventoryApiFixture.ReadAsync(response)));
        });

        Assert.Single(outcomes, outcome => outcome.StatusCode == HttpStatusCode.Created);
        Assert.All(outcomes, outcome => Assert.Contains(outcome.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Single(outcomes.Select(outcome => outcome.Id).Distinct());
        Assert.Equal((3, 2, 1), await BalanceAsync(item));
    }

    [Fact]
    public async Task ConcurrentCommitAndRelease_TransitionAReservationExactlyOnce()
    {
        fixture.RequireAvailable();
        for (var round = 0; round < 15; round++)
        {
            var tenantId = fixture.NewTenant(out var owner, out _, out _);
            var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 10);
            var id = await ReserveIdAsync(tenantId, [Line(item, 4)]);

            var outcomes = await ConcurrentlyAsync(6, async caller =>
            {
                using var response = await TransitionAsync(tenantId, id, caller % 2 == 0 ? "commit" : "release");
                return response.StatusCode;
            });

            var status = await StatusOfAsync(id);
            Assert.Contains(status, new[] { "Committed", "Released" });
            Assert.Equal(status == "Committed" ? (6, 0, 6) : (10, 0, 10), await BalanceAsync(item));
            Assert.Equal(1, await EventCountAsync(tenantId, "ReservationCommitted") + await EventCountAsync(tenantId, "ReservationReleased"));
            Assert.All(outcomes, outcome => Assert.Contains(outcome, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
            await AssertLedgerMatchesAsync(item);
        }
    }

    [Fact]
    public async Task AdjustmentsAndReservations_Concurrently_KeepTheInvariant()
    {
        fixture.RequireAvailable();
        var tenantId = fixture.NewTenant(out var owner, out _, out _);
        var item = await fixture.CreateItemAsync(tenantId, owner, onHand: 20);
        var path = $"/api/v1/inventory/tenants/{tenantId}/items/{item.GetProperty("id").GetGuid()}/adjustments";

        await ConcurrentlyAsync(24, async caller =>
        {
            if (caller % 3 == 0)
            {
                using var adjustment = await fixture.SendAsync(fixture.UserToken(owner), HttpMethod.Post, path, new { quantityChange = -2, reason = "Loss" }, InventoryApiFixture.Key());
                return adjustment.StatusCode;
            }

            return await ReserveStatusAsync(tenantId, [Line(item, 1)]);
        });

        var (onHand, reserved, available) = await BalanceAsync(item);
        Assert.True(onHand >= reserved && reserved >= 0 && available == onHand - reserved, $"{onHand}/{reserved}/{available}");
        await AssertLedgerMatchesAsync(item);
    }

    private static object Line(JsonElement item, int quantity) => new { variantId = item.GetProperty("variantId").GetGuid(), quantity };

    private Task<HttpResponseMessage> ReserveAsync(Guid tenantId, object[] lines, string? key = null, string? reference = null, string client = TestTokenIssuer.ReservationClient) =>
        fixture.SendAsync(fixture.ServiceToken(client), HttpMethod.Post, Reservations(tenantId), new { lines, reference }, InventoryApiFixture.Key(key));

    private async Task<HttpStatusCode> ReserveStatusAsync(Guid tenantId, object[] lines)
    {
        using var response = await ReserveAsync(tenantId, lines);
        return response.StatusCode;
    }

    private async Task<Guid> ReserveIdAsync(Guid tenantId, object[] lines)
    {
        using var response = await ReserveAsync(tenantId, lines);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return Id(await InventoryApiFixture.ReadAsync(response));
    }

    private Task<HttpResponseMessage> TransitionAsync(Guid tenantId, Guid id, string transition) =>
        fixture.SendAsync(fixture.ServiceToken(), HttpMethod.Post, $"{Reservations(tenantId)}/{id}/{transition}");

    private async Task<(int OnHand, int Reserved, int Available)> BalanceAsync(JsonElement item)
    {
        var values = (await fixture.Database.ScalarAsync<string>($"SELECT CONCAT(OnHand, '|', Reserved) FROM InventoryItems WHERE Id = '{item.GetProperty("id").GetGuid()}'")).Split('|').Select(int.Parse).ToArray();
        return (values[0], values[1], values[0] - values[1]);
    }

    private async Task AssertLedgerMatchesAsync(JsonElement item)
    {
        var id = item.GetProperty("id").GetGuid();
        Assert.Equal(
            await fixture.Database.ScalarAsync<string>($"SELECT CONCAT(OnHand, '|', Reserved, '|', Version) FROM InventoryItems WHERE Id = '{id}'"),
            await fixture.Database.ScalarAsync<string>($"SELECT CONCAT(SUM(OnHandDelta), '|', SUM(ReservedDelta), '|', MAX(ItemVersion)) FROM StockMovements WHERE InventoryItemId = '{id}'"));
        Assert.Equal(0, await fixture.Database.ScalarAsync<int>(
            $"SELECT COUNT(*) FROM StockMovements m JOIN StockMovements p ON p.InventoryItemId = m.InventoryItemId AND p.ItemVersion = m.ItemVersion - 1 WHERE m.InventoryItemId = '{id}' AND (m.OnHandBefore <> p.OnHandAfter OR m.ReservedBefore <> p.ReservedAfter)"));
    }

    private Task MakeOverdueAsync(Guid reservationId) =>
        fixture.Database.ExecuteAsync($"UPDATE Reservations SET CreatedAt = DATEADD(MINUTE, -30, SYSUTCDATETIME()), ExpiresAt = DATEADD(MINUTE, -15, SYSUTCDATETIME()) WHERE Id = '{reservationId}'");

    private Task<string> StatusOfAsync(Guid reservationId) =>
        fixture.Database.ScalarAsync<string>($"SELECT Status FROM Reservations WHERE Id = '{reservationId}'");

    private Task<int> EventCountAsync(Guid tenantId, string? eventType = null) =>
        fixture.Database.ScalarAsync<int>($"SELECT COUNT(*) FROM OutboxMessages WHERE MessageKey = '{tenantId}'" + (eventType is null ? string.Empty : $" AND EventType = '{eventType}'"));

    private async Task<IReadOnlyList<string>> EnvelopesAsync(Guid tenantId) =>
        (await fixture.Database.ScalarAsync<string>($"SELECT STRING_AGG(CAST(Envelope AS nvarchar(max)), CHAR(30)) FROM OutboxMessages WHERE MessageKey = '{tenantId}'")).Split('\u001e');

    private static async Task<T[]> ConcurrentlyAsync<T>(int callers, Func<int, Task<T>> call)
    {
        using var start = new Barrier(callers);
        return await Task.WhenAll(Enumerable.Range(0, callers).Select(caller => Task.Run(async () =>
        {
            start.SignalAndWait(TimeSpan.FromSeconds(30));
            return await call(caller);
        })));
    }

    private static Guid Id(JsonElement reservation) => reservation.GetProperty("id").GetGuid();

    private static string Status(JsonElement reservation) => reservation.GetProperty("status").GetString()!;

    private static string Reservations(Guid tenantId) => $"/internal/v1/inventory/tenants/{tenantId}/reservations";
}
