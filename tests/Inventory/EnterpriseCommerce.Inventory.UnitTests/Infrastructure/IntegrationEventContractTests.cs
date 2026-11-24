using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.Inventory.Application.Common;
using EnterpriseCommerce.Inventory.Domain.Common;
using EnterpriseCommerce.Inventory.Domain.Items;
using EnterpriseCommerce.Inventory.Domain.Reservations;
using EnterpriseCommerce.Inventory.Infrastructure.Messaging;
using EnterpriseCommerce.Inventory.UnitTests.Domain;

namespace EnterpriseCommerce.Inventory.UnitTests.Infrastructure;

/// <summary>
/// The producer side of Inventory's v1 contracts (ADR-028 section 2, ADR-023 sections 3 and 4): the mapper, the
/// envelope values, the excluded data, and conformance with the committed schemas and golden samples.
/// </summary>
public sealed class IntegrationEventContractTests
{
    private const string Topic = "inventory.events";
    private const string Correlation = "7c1e2d3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f";
    private static readonly MessageMetadata Metadata = MessageMetadata.ForRequest(Correlation)!;

    [Fact]
    public void AMovement_MapsToOneStockLevelChangedV1Row_KeyedByTheTenant_WithTheMovementAsItsFact()
    {
        var item = DomainFixtures.Item(onHand: 12);
        var movement = item.PendingMovements[0];

        var row = Assert.Single(Map(item));

        Assert.Equal((Topic, item.TenantId.ToString("D"), "StockLevelChanged", 1, movement.Id), (row.Topic, row.MessageKey, row.EventType, row.EventVersion, row.EventId));
        using var envelope = JsonDocument.Parse(row.Envelope);
        var root = envelope.RootElement;
        Assert.Equal(("inventory", item.Id.ToString("D"), item.TenantId.ToString("D")), (root.GetProperty("producer").GetString(), root.GetProperty("aggregateId").GetString(), root.GetProperty("tenantId").GetString()));
        Assert.Equal((Correlation, Correlation), (root.GetProperty("correlationId").GetString(), root.GetProperty("causationId").GetString()));
        var payload = root.GetProperty("payload");
        Assert.Equal(("InitialStock", JsonValueKind.Null, 12, 0, 12, 12, 1L), (
            payload.GetProperty("movementType").GetString(),
            payload.GetProperty("adjustmentReason").ValueKind,
            payload.GetProperty("onHandDelta").GetInt32(),
            payload.GetProperty("reserved").GetInt32(),
            payload.GetProperty("onHand").GetInt32(),
            payload.GetProperty("available").GetInt32(),
            payload.GetProperty("itemVersion").GetInt64()));
        Assert.Equal(item.Sku, payload.GetProperty("sku").GetString());
        EventSchemas.AssertValid(row.Envelope);
    }

    [Fact]
    public void AnAdjustment_PublishesItsReason()
    {
        var item = DomainFixtures.Item(onHand: 12);
        item.ClearDomainEvents();
        item.Adjust(-2, AdjustmentReason.Damage, Actor.User(DomainFixtures.Subject), DomainFixtures.Key("adj"), DomainFixtures.Correlation, DomainFixtures.Now);

        var row = Assert.Single(Map(item));

        using var envelope = JsonDocument.Parse(row.Envelope);
        var payload = envelope.RootElement.GetProperty("payload");
        Assert.Equal(("Adjustment", "Damage", -2, 10, 2L), (payload.GetProperty("movementType").GetString(), payload.GetProperty("adjustmentReason").GetString(), payload.GetProperty("onHandDelta").GetInt32(), payload.GetProperty("onHand").GetInt32(), payload.GetProperty("itemVersion").GetInt64()));
        EventSchemas.AssertValid(row.Envelope);
    }

    [Fact]
    public void EveryReservationTransition_MapsToItsOwnEventType_WithItsLinesAndVersion()
    {
        var item = DomainFixtures.Item(onHand: 10);
        var committed = DomainFixtures.Reserve(item, 1);
        committed.Commit(DomainFixtures.ById(item), Actor.Service(DomainFixtures.Client), DomainFixtures.Correlation, DomainFixtures.Now);
        var released = DomainFixtures.Reserve(item, 1);
        released.Release(DomainFixtures.ById(item), Actor.Service(DomainFixtures.Client), DomainFixtures.Correlation, DomainFixtures.Now);
        var expired = DomainFixtures.Reserve(item, 1, TimeSpan.FromMinutes(1));
        expired.Expire(DomainFixtures.ById(item), DomainFixtures.Correlation, DomainFixtures.Now.AddMinutes(1));

        var rows = Map(committed).Concat(Map(released)).Concat(Map(expired)).ToList();

        Assert.Equal(
            ["ReservationCreated", "ReservationCommitted", "ReservationCreated", "ReservationReleased", "ReservationCreated", "ReservationExpired"],
            rows.Select(row => row.EventType));
        foreach (var row in rows)
        {
            EventSchemas.AssertValid(row.Envelope);
            using var envelope = JsonDocument.Parse(row.Envelope);
            var payload = envelope.RootElement.GetProperty("payload");
            Assert.Equal(row.EventType == "ReservationCreated" ? 1 : 2, payload.GetProperty("reservationVersion").GetInt64());
            var line = Assert.Single(payload.GetProperty("lines").EnumerateArray());
            Assert.Equal((item.Id.ToString("D"), 1), (line.GetProperty("inventoryItemId").GetString(), line.GetProperty("quantity").GetInt32()));
            Assert.Equal("order-1", payload.GetProperty("reference").GetString());
        }

        Assert.Equal(["Committed", "Released", "Expired"], rows.Where(row => row.EventType != "ReservationCreated").Select(Status));
    }

    [Fact]
    public void TheMessages_CarryNoClientSubjectKeyHashOrActor()
    {
        // ADR-023 section 4: the owning client, the merchant subject, the idempotency keys and hashes stay in Inventory.
        var item = DomainFixtures.Item(onHand: 10);
        item.Adjust(1, AdjustmentReason.Receipt, Actor.User(DomainFixtures.Subject), DomainFixtures.Key("secret-adjust-key"), DomainFixtures.Correlation, DomainFixtures.Now);
        var reservation = DomainFixtures.Place([(item, 2)], key: "secret-reserve-key").Reservation!;

        foreach (var row in Map(item).Concat(Map(reservation)))
        {
            foreach (var forbidden in new[] { DomainFixtures.Subject, DomainFixtures.Client, "secret-adjust-key", "secret-reserve-key", "key-1", "reservation-expiry", Convert.ToBase64String(reservation.RequestHash), Convert.ToHexString(reservation.RequestHash) })
            {
                Assert.DoesNotContain(forbidden, row.Envelope, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void AFiftyLineReservation_FitsTheEnvelopeLimit()
    {
        var items = Enumerable.Range(0, Reservation.MaxLines).Select(_ => DomainFixtures.Item(onHand: 5)).ToList();
        var reservation = DomainFixtures.Place([.. items.Select(item => (item, 1))]).Reservation!;

        var row = Assert.Single(Map(reservation));

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(row.Envelope) < OutboxMessage.EnvelopeMaxBytes / 4);
        EventSchemas.AssertValid(row.Envelope);
    }

    [Fact]
    public void AnUnknownDomainEvent_HasNoSilentMapping()
    {
        Assert.Throws<InvalidOperationException>(() => InventoryIntegrationEvents.ToFact(new UnmappedEvent(DomainFixtures.Now)));
    }

    public static TheoryData<string> EventTypes() => [.. EventSchemas.EventTypes];

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void TheGoldenSample_IsValid_AndRoundTripsWithoutDataLoss(string eventType)
    {
        // ADR-023 section 3: every sample ever published must still deserialize into the current contract.
        var sample = EventSchemas.ReadText(EventSchemas.SampleFile(eventType));
        EventSchemas.AssertValid(sample);

        var reserialized = eventType == "StockLevelChanged"
            ? IntegrationEventSerializer.Serialize(JsonSerializer.Deserialize<IntegrationEventEnvelope<StockLevelChangedV1Payload>>(sample, IntegrationEventSerializer.Options)!)
            : IntegrationEventSerializer.Serialize(JsonSerializer.Deserialize<IntegrationEventEnvelope<ReservationV1Payload>>(sample, IntegrationEventSerializer.Options)!);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sample), JsonNode.Parse(reserialized)));
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public void TheMappersOutput_HasTheShapeOfTheGoldenSample(string eventType)
    {
        var sample = JsonNode.Parse(EventSchemas.ReadText(EventSchemas.SampleFile(eventType)))!;
        var item = DomainFixtures.Item(onHand: 10);
        var second = DomainFixtures.Item(onHand: 10);
        var reservation = DomainFixtures.Place([(item, 2), (second, 1)]).Reservation!;
        _ = eventType switch
        {
            "ReservationCommitted" => reservation.Commit(DomainFixtures.ById(item, second), Actor.Service(DomainFixtures.Client), DomainFixtures.Correlation, DomainFixtures.Now),
            "ReservationReleased" => reservation.Release(DomainFixtures.ById(item, second), Actor.Service(DomainFixtures.Client), DomainFixtures.Correlation, DomainFixtures.Now),
            "ReservationExpired" => reservation.Expire(DomainFixtures.ById(item, second), DomainFixtures.Correlation, DomainFixtures.Now.AddHours(1)),
            _ => default,
        };

        var row = (eventType == "StockLevelChanged" ? Map(item) : Map(reservation)).Last(candidate => candidate.EventType == eventType);

        Assert.Equal(Shape(sample), Shape(JsonNode.Parse(row.Envelope)!));
    }

    public static TheoryData<string, Type> ContractTypes() => new()
    {
        { "envelope.v1.schema.json", typeof(IntegrationEventEnvelope<StockLevelChangedV1Payload>) },
        { EventSchemas.PayloadSchemaFile("StockLevelChanged"), typeof(StockLevelChangedV1Payload) },
        { EventSchemas.PayloadSchemaFile("ReservationCreated"), typeof(ReservationV1Payload) },
        { EventSchemas.PayloadSchemaFile("ReservationCommitted"), typeof(ReservationV1Payload) },
        { EventSchemas.PayloadSchemaFile("ReservationReleased"), typeof(ReservationV1Payload) },
        { EventSchemas.PayloadSchemaFile("ReservationExpired"), typeof(ReservationV1Payload) },
    };

    [Theory]
    [MemberData(nameof(ContractTypes))]
    public void TheGeneratedSchema_MatchesTheCommittedSchemasStructure(string schemaFile, Type contract)
    {
        // ADR-023 section 3: the in-box exporter's schema of the contract type against the committed file. The committed
        // file keeps its hand-written pattern, length and range constraints; the structure (property names, types and
        // required properties) must agree.
        var generated = JsonSchemaExporter.GetJsonSchemaAsNode(IntegrationEventSerializer.Options, contract).AsObject();
        var committed = JsonNode.Parse(EventSchemas.ReadText(schemaFile))!.AsObject();

        var committedProperties = committed["properties"]!.AsObject();
        var generatedProperties = generated["properties"]!.AsObject();
        Assert.Equal(committedProperties.Select(property => property.Key).Order(), generatedProperties.Select(property => property.Key).Order());
        Assert.Equal(
            committed["required"]!.AsArray().Select(name => name!.GetValue<string>()).Order(),
            generated["required"]!.AsArray().Select(name => name!.GetValue<string>()).Order());

        foreach (var (name, committedProperty) in committedProperties)
        {
            var committedType = committedProperty!["type"]?.ToJsonString();
            var generatedType = (generatedProperties[name] as JsonObject)?["type"]?.ToJsonString();

            // A property written by a custom converter (the UTC timestamp) has no generated type; the encoding tests cover
            // its format. The envelope's payload is an object in the committed schema only (validated in the second step).
            if (generatedType is not null)
            {
                Assert.True(committedType == generatedType, $"{schemaFile}.{name}: committed type {committedType}, generated {generatedType}.");
            }
        }
    }

    private static string Status(OutboxMessage row)
    {
        using var envelope = JsonDocument.Parse(row.Envelope);
        return envelope.RootElement.GetProperty("payload").GetProperty("status").GetString()!;
    }

    private static string Shape(JsonNode node) => node switch
    {
        JsonObject json => "{" + string.Join(",", json.Select(property => $"{property.Key}:{(property.Value is null ? "Null" : Shape(property.Value))}")) + "}",
        JsonArray array => "[" + string.Join(",", array.Take(1).Select(element => Shape(element!))) + "]",
        _ => node.GetValueKind().ToString(),
    };

    private static List<OutboxMessage> Map(IHasDomainEvents aggregate) =>
        [.. aggregate.DomainEvents.SelectMany(domainEvent => InventoryIntegrationEvents.ToOutboxMessages(domainEvent, Metadata, Topic))];

    private sealed record UnmappedEvent(DateTimeOffset OccurredAt) : IDomainEvent;
}
