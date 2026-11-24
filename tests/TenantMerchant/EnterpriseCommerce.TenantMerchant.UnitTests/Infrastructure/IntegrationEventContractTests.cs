using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using EnterpriseCommerce.TenantMerchant.Application.Common;
using EnterpriseCommerce.TenantMerchant.Domain.Memberships;
using EnterpriseCommerce.TenantMerchant.Domain.Tenants;
using EnterpriseCommerce.BuildingBlocks.Messaging;
using EnterpriseCommerce.TenantMerchant.Infrastructure.Messaging;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>
/// The producer side of the <c>TenantCreated</c> v1 contract (ADR-023 sections 3, 4 and 9; PHASE_3_S4_EVENT_FLOW.md
/// section 8): the mapper, the envelope encoding, the pre-write guards, and conformance with the committed schemas.
/// </summary>
public sealed class IntegrationEventContractTests
{
    private const string Topic = "tenant-merchant.events";
    private const string Correlation = "3f2a9c1e-8b4d-4e6f-a1c2-7d9e0b3a5f68";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 10, 15, 30, 123, TimeSpan.Zero);
    private static readonly MessageMetadata Metadata = MessageMetadata.ForRequest(Correlation)!;

    [Fact]
    public void AProvisionedTenant_MapsToOneTenantCreatedV1Row_WithEveryEnvelopeField()
    {
        var tenant = Provisioned("northwind", "Northwind Goods");

        var row = Assert.Single(Map(tenant));

        Assert.Equal((Topic, tenant.Id.ToString("D"), "TenantCreated", 1, Now), (row.Topic, row.MessageKey, row.EventType, row.EventVersion, row.OccurredAt));
        Assert.Equal(7, row.EventId.Version);
        Assert.Null(row.PublishedAt);

        using var envelope = JsonDocument.Parse(row.Envelope);
        var root = envelope.RootElement;
        Assert.Equal(row.EventId.ToString("D"), root.GetProperty("eventId").GetString());
        Assert.Equal("TenantCreated", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("eventVersion").GetInt32());
        Assert.Equal([1], root.GetProperty("publishedVersions").EnumerateArray().Select(version => version.GetInt32()));
        Assert.Equal("tenant-merchant", root.GetProperty("producer").GetString());
        Assert.Equal(tenant.Id.ToString("D"), root.GetProperty("aggregateId").GetString());
        Assert.Equal(tenant.Id.ToString("D"), root.GetProperty("tenantId").GetString());
        Assert.Equal("2026-11-23T10:15:30.123Z", root.GetProperty("occurredAtUtc").GetString());

        // A root message caused by an HTTP request: causationId == correlationId (ADR-023 section 4).
        Assert.Equal(Correlation, root.GetProperty("correlationId").GetString());
        Assert.Equal(Correlation, root.GetProperty("causationId").GetString());

        var payload = root.GetProperty("payload");
        Assert.Equal(("northwind", "Northwind Goods", "Active", "Provisioned"), (
            payload.GetProperty("slug").GetString(),
            payload.GetProperty("displayName").GetString(),
            payload.GetProperty("status").GetString(),
            payload.GetProperty("origin").GetString()));
        EventSchemas.AssertValidTenantCreatedV1(row.Envelope);
    }

    [Fact]
    public void ASelfServiceTenant_MapsToTheSameEvent_WithTheSelfServiceOrigin()
    {
        var row = Assert.Single(Map(SelfService("northwind", "Northwind Goods")));

        using var envelope = JsonDocument.Parse(row.Envelope);
        Assert.Equal("SelfService", envelope.RootElement.GetProperty("payload").GetProperty("origin").GetString());
        EventSchemas.AssertValidTenantCreatedV1(row.Envelope);
    }

    [Fact]
    public void TheEnvelope_HasExactlyTheContractsCamelCaseProperties()
    {
        var row = Assert.Single(Map(Provisioned("northwind", "Northwind Goods")));

        var envelope = JsonNode.Parse(row.Envelope)!.AsObject();
        Assert.Equal(
            ["eventId", "eventType", "eventVersion", "publishedVersions", "producer", "aggregateId", "tenantId", "occurredAtUtc", "correlationId", "causationId", "payload"],
            envelope.Select(property => property.Key));
        Assert.Equal(["slug", "displayName", "status", "origin"], envelope["payload"]!.AsObject().Select(property => property.Key));
    }

    [Fact]
    public void GuidsAreLowercase_AndTimestampsAreUtcWithZ_WhateverTheInputOffset()
    {
        var tenant = Tenant.Provision(Slug("northwind"), Name("Northwind Goods"), ProvisioningRecordOf("svc-admin", "key-1", "admin:1"), new DateTimeOffset(2026, 11, 23, 13, 15, 30, TimeSpan.FromHours(3)));

        var row = Assert.Single(Map(tenant));

        using var envelope = JsonDocument.Parse(row.Envelope);
        Assert.Equal("2026-11-23T10:15:30Z", envelope.RootElement.GetProperty("occurredAtUtc").GetString());
        Assert.Equal(row.MessageKey.ToLowerInvariant(), row.MessageKey);
        Assert.Equal(row.EventId.ToString("D").ToLowerInvariant(), envelope.RootElement.GetProperty("eventId").GetString());
    }

    [Fact]
    public void TheMessage_CarriesNoSubjectClientKeyHashOrActor()
    {
        // ADR-023 sections 4 and 9: the initial owner's identity, the client ID, the idempotency key, the request hash
        // and the actor reference stay in Tenant & Merchant.
        var hash = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var provisioned = Tenant.Provision(Slug("northwind"), Name("Northwind Goods"), new ProvisioningRecord("svc-secret-client", "idem-key-provisioned", hash, Actor("actor:secret-7")), Now);
        var selfService = Tenant.CreateBySelfService(Slug("southwind"), Name("Southwind"), new SelfServiceRecord("bff-secret-client", "idem-key-self", hash, Subject("subject-secret-9")), Now);

        foreach (var row in Map(provisioned).Concat(Map(selfService)))
        {
            foreach (var forbidden in new[] { "svc-secret-client", "idem-key-provisioned", "actor:secret-7", "bff-secret-client", "idem-key-self", "subject-secret-9", Convert.ToBase64String(hash), Convert.ToHexString(hash), Convert.ToHexStringLower(hash) })
            {
                Assert.DoesNotContain(forbidden, row.Envelope, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void EveryDomainEvent_GetsItsOwnFactIdentity()
    {
        var first = Assert.Single(Map(Provisioned("northwind", "Northwind Goods")));
        var second = Assert.Single(Map(Provisioned("southwind", "Southwind")));

        Assert.NotEqual(first.EventId, second.EventId);
    }

    [Fact]
    public void ADualPublishedFact_SharesOneEventIdAndOnePublishedVersionsList_AcrossItsVersions()
    {
        // S4 publishes TenantCreated in v1 only. A synthetic fact shows the dual-publication shape of ADR-023 section 3.
        var tenantId = Guid.CreateVersion7(Now);
        var fact = new IntegrationEventFact(
            Guid.CreateVersion7(Now),
            "SyntheticHappened",
            tenantId,
            tenantId,
            Now,
            [new IntegrationEventVersion(2, new { name = "v2" }), new IntegrationEventVersion(1, new { name = "v1" })]);

        var rows = IntegrationEventSerializer.ToOutboxMessages(fact, "tenant-merchant", Metadata.CorrelationId, Metadata.CausationId, Topic);

        Assert.Equal([2, 1], rows.Select(row => row.EventVersion));
        Assert.All(rows, row =>
        {
            Assert.Equal((fact.EventId, tenantId.ToString("D"), Topic), (row.EventId, row.MessageKey, row.Topic));
            using var envelope = JsonDocument.Parse(row.Envelope);
            Assert.Equal(fact.EventId.ToString("D"), envelope.RootElement.GetProperty("eventId").GetString());
            Assert.Equal(row.EventVersion, envelope.RootElement.GetProperty("eventVersion").GetInt32());
            Assert.Equal([1, 2], envelope.RootElement.GetProperty("publishedVersions").EnumerateArray().Select(version => version.GetInt32()));
            Assert.True(EventSchemas.IsValidEnvelope(envelope.RootElement));
        });
    }

    [Fact]
    public void AFactListingAVersionTwice_IsRefusedBeforeTheWrite()
    {
        var fact = new IntegrationEventFact(Guid.CreateVersion7(Now), "SyntheticHappened", Guid.CreateVersion7(Now), null, Now, [new IntegrationEventVersion(1, new { }), new IntegrationEventVersion(1, new { })]);

        Assert.Throws<InvalidOperationException>(() => IntegrationEventSerializer.ToOutboxMessages(fact, "tenant-merchant", Metadata.CorrelationId, Metadata.CausationId, Topic));
    }

    [Fact]
    public void AnEnvelopeWhoseVersionIsNotPublished_IsRefusedBeforeTheWrite()
    {
        var envelope = Envelope() with { EventVersion = 2, PublishedVersions = [1] };

        Assert.Throws<InvalidOperationException>(() => IntegrationEventSerializer.Serialize(envelope));
    }

    public static TheoryData<string> MalformedEnvelopes() =>
    [
        "unsorted publishedVersions",
        "empty publishedVersions",
        "version 4 event ID",
        "lowercase event type",
        "invalid producer",
        "empty correlation ID",
        "long causation ID",
        "correlation ID with a space",
    ];

    [Theory]
    [MemberData(nameof(MalformedEnvelopes))]
    public void AMalformedEnvelope_IsRefusedBeforeTheWrite(string defect)
    {
        var valid = Envelope();
        var envelope = defect switch
        {
            "unsorted publishedVersions" => valid with { PublishedVersions = [2, 1] },
            "empty publishedVersions" => valid with { PublishedVersions = [] },
            "version 4 event ID" => valid with { EventId = Guid.NewGuid() },
            "lowercase event type" => valid with { EventType = "tenantCreated" },
            "invalid producer" => valid with { Producer = "Tenant_Merchant" },
            "empty correlation ID" => valid with { CorrelationId = string.Empty },
            "long causation ID" => valid with { CausationId = new string('a', 65) },
            "correlation ID with a space" => valid with { CorrelationId = "a b" },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };

        Assert.Throws<InvalidOperationException>(() => IntegrationEventSerializer.Serialize(envelope));
    }

    [Fact]
    public void AnEnvelopeOver64KiB_IsRefusedBeforeTheWrite()
    {
        var atLimit = Envelope() with { Payload = new TenantCreatedV1Payload("northwind", string.Empty, "Active", "Provisioned") };
        var overhead = Encoding.UTF8.GetByteCount(IntegrationEventSerializer.Serialize(atLimit));

        var fits = atLimit with { Payload = atLimit.Payload with { DisplayName = new string('a', OutboxMessage.EnvelopeMaxBytes - overhead) } };
        var tooLarge = atLimit with { Payload = atLimit.Payload with { DisplayName = new string('a', OutboxMessage.EnvelopeMaxBytes - overhead + 1) } };

        Assert.Equal(OutboxMessage.EnvelopeMaxBytes, Encoding.UTF8.GetByteCount(IntegrationEventSerializer.Serialize(fits)));
        Assert.Throws<InvalidOperationException>(() => IntegrationEventSerializer.Serialize(tooLarge));
    }

    [Fact]
    public void TheGoldenSample_RoundTripsWithoutDataLoss()
    {
        // ADR-023 section 3: every sample ever published must still deserialize into the current contract.
        var sample = EventSchemas.ReadText(Path.Combine("tenant-merchant", "samples", "TenantCreated.v1.json"));

        var envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope<TenantCreatedV1Payload>>(sample, IntegrationEventSerializer.Options)!;

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sample), JsonNode.Parse(IntegrationEventSerializer.Serialize(envelope))));
        EventSchemas.AssertValidTenantCreatedV1(sample);
    }

    [Fact]
    public void TheMappersOutput_HasTheShapeOfTheGoldenSample()
    {
        var sample = JsonNode.Parse(EventSchemas.ReadText(Path.Combine("tenant-merchant", "samples", "TenantCreated.v1.json")))!;
        var row = Assert.Single(Map(SelfService("northwind-goods", "Northwind Goods")));

        Assert.Equal(Shape(sample), Shape(JsonNode.Parse(row.Envelope)!));

        static string Shape(JsonNode node) => node switch
        {
            JsonObject json => "{" + string.Join(",", json.Select(property => $"{property.Key}:{Shape(property.Value!)}")) + "}",
            JsonArray array => "[" + string.Join(",", array.Select(item => Shape(item!))) + "]",
            _ => node.GetValueKind().ToString(),
        };
    }

    public static TheoryData<string, string> ContractTypes() => new()
    {
        { "envelope.v1.schema.json", "envelope" },
        { Path.Combine("tenant-merchant", "TenantCreated.v1.schema.json"), "TenantCreated v1" },
    };

    [Theory]
    [MemberData(nameof(ContractTypes))]
    public void TheGeneratedSchema_MatchesTheCommittedSchemasStructure(string schemaFile, string contract)
    {
        // ADR-023 section 3: the in-box exporter's schema of the contract type against the committed file. The committed
        // file keeps its hand-written pattern, length and token constraints; the structure (property names, types and
        // required properties) must agree.
        var type = contract == "envelope" ? typeof(IntegrationEventEnvelope<TenantCreatedV1Payload>) : typeof(TenantCreatedV1Payload);
        var generated = JsonSchemaExporter.GetJsonSchemaAsNode(IntegrationEventSerializer.Options, type).AsObject();
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

            // A property written by a custom converter (the UTC timestamp) has no generated type; the encoding tests
            // cover its format. The envelope's payload is typed as an object in the committed schema only: its content
            // is validated by the payload schema (the second validation step).
            if (generatedType is not null)
            {
                Assert.True(committedType == generatedType, $"{contract}.{name}: committed type {committedType}, generated {generatedType}.");
            }
        }
    }

    public static TheoryData<string, bool> DisplayNames()
    {
        // PHASE_3_S4_EVENT_FLOW.md section 8: leading and trailing white space of every .NET white-space character,
        // interior spaces, non-BMP characters, U+FEFF, 200 and 201 code units, and white space only.
        var data = new TheoryData<string, bool>
        {
            { "Northwind Goods", true },
            { "Café \U0001F600 Store", true },
            { "﻿Shop", true },
            { "Shop﻿", true },
            { new string('a', 200), true },
            { new string('a', 201), false },
            { string.Concat(Enumerable.Repeat("\U0001F600", 100)), true },
            { "Bad\u0007Name", false },
            { "   ", false },
            { string.Empty, false },
        };
        foreach (var white in WhiteSpace())
        {
            data.Add($"{white}Shop{white}", true);
            data.Add($"A{white}B", !char.IsControl(white));
            data.Add(new string(white, 3), false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DisplayNames))]
    public void ADisplayNameTheDomainAccepts_IsAValidPayload_AndOneItRefusesIsNeverWritten(string input, bool accepted)
    {
        Assert.Equal(accepted, MerchantDisplayName.TryCreate(input, out var name));
        if (!accepted)
        {
            return;
        }

        var row = Assert.Single(Map(Tenant.Provision(Slug("northwind"), name!, ProvisioningRecordOf("svc-admin", "key-1", "admin:1"), Now)));
        EventSchemas.AssertValidTenantCreatedV1(row.Envelope);
        using var envelope = JsonDocument.Parse(row.Envelope);
        Assert.Equal(name!.Value, envelope.RootElement.GetProperty("payload").GetProperty("displayName").GetString());
    }

    [Fact]
    public void TheSchemasDisplayNameRule_EqualsTheDomainRule_ForEveryBmpCharacterInThreePositions()
    {
        // The design review's differential check (PHASE_3_S4_EVENT_FLOW.md section 10), repeated in .NET: a value is
        // emittable when MerchantDisplayName accepts it and stores it unchanged. The schema must accept exactly those.
        var mismatches = new List<string>();
        for (var codePoint = 0; codePoint <= 0xFFFF; codePoint++)
        {
            if (codePoint is >= 0xD800 and <= 0xDFFF)
            {
                continue; // An unpaired surrogate cannot reach a message (System.Text.Json refuses it).
            }

            var character = ((char)codePoint).ToString();
            foreach (var candidate in new[] { character, $"a{character}", $"a{character}b" })
            {
                var emittable = MerchantDisplayName.TryCreate(candidate, out var name) && name!.Value == candidate;
                if (emittable != SchemaAcceptsDisplayName(candidate))
                {
                    mismatches.Add($"U+{codePoint:X4} in \"{Uri.EscapeDataString(candidate)}\": domain {emittable}");
                }
            }
        }

        Assert.Empty(mismatches);
    }

    [Fact]
    public void AnUnknownDomainEvent_HasNoSilentMapping()
    {
        Assert.Throws<InvalidOperationException>(() => TenantMerchantIntegrationEvents.ToFact(new UnmappedEvent(Now)));
    }

    private static bool SchemaAcceptsDisplayName(string displayName)
    {
        var payload = new JsonObject { ["slug"] = "northwind", ["displayName"] = displayName, ["status"] = "Active", ["origin"] = "Provisioned" };
        using var document = JsonDocument.Parse(payload.ToJsonString());
        return EventSchemas.IsValidTenantCreatedV1Payload(document.RootElement);
    }

    private static IEnumerable<char> WhiteSpace() =>
        Enumerable.Range(0, 0x10000).Select(codePoint => (char)codePoint).Where(char.IsWhiteSpace);

    private static IReadOnlyList<OutboxMessage> Map(Tenant tenant) =>
        [.. tenant.DomainEvents.SelectMany(domainEvent => TenantMerchantIntegrationEvents.ToOutboxMessages(domainEvent, Metadata, Topic))];

    private static IntegrationEventEnvelope<TenantCreatedV1Payload> Envelope()
    {
        var tenantId = Guid.CreateVersion7(Now);
        return new IntegrationEventEnvelope<TenantCreatedV1Payload>(
            Guid.CreateVersion7(Now), "TenantCreated", 1, [1], "tenant-merchant", tenantId, tenantId, Now, Correlation, Correlation,
            new TenantCreatedV1Payload("northwind", "Northwind Goods", "Active", "Provisioned"));
    }

    private static Tenant Provisioned(string slug, string displayName) =>
        Tenant.Provision(Slug(slug), Name(displayName), ProvisioningRecordOf("svc-admin", "key-1", "admin:1"), Now);

    private static Tenant SelfService(string slug, string displayName) =>
        Tenant.CreateBySelfService(Slug(slug), Name(displayName), new SelfServiceRecord("bff-merchant", "key-1", new byte[32], Subject("user-1")), Now);

    private static ProvisioningRecord ProvisioningRecordOf(string client, string key, string actor) => new(client, key, new byte[32], Actor(actor));

    private static TenantSlug Slug(string value) => TenantSlug.TryCreate(value, out var slug) ? slug : throw new ArgumentException(value);

    private static MerchantDisplayName Name(string value) => MerchantDisplayName.TryCreate(value, out var name) ? name! : throw new ArgumentException(value);

    private static ActorReference Actor(string value) => ActorReference.TryCreate(value, out var actor) ? actor : throw new ArgumentException(value);

    private static SubjectId Subject(string value) => SubjectId.TryCreate(value, out var subject) ? subject : throw new ArgumentException(value);

    private sealed record UnmappedEvent(DateTimeOffset OccurredAt) : EnterpriseCommerce.TenantMerchant.Domain.Common.IDomainEvent;
}
