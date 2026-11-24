using System.Text.Json;
using Corvus.Json;
using ValidatorSchema = Corvus.Json.Validator.JsonSchema;

namespace EnterpriseCommerce.Inventory.UnitTests.Infrastructure;

/// <summary>
/// The committed event schemas under <c>docs/events/schemas</c>, compiled once, with <c>format</c> asserted. Messages
/// are validated in the two steps of docs/events/README.md: the envelope, then the payload by its own schema.
/// </summary>
internal static class EventSchemas
{
    /// <summary>The Inventory event types and their payload schemas (ADR-028 section 2), all version 1.</summary>
    public static readonly IReadOnlyList<string> EventTypes =
        ["StockLevelChanged", "ReservationCreated", "ReservationCommitted", "ReservationReleased", "ReservationExpired"];

    private static readonly Lazy<string> SchemaRoot = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnterpriseCommerce.sln")))
            {
                return Path.Combine(directory.FullName, "docs", "events", "schemas");
            }
        }

        throw new InvalidOperationException("The repository root (EnterpriseCommerce.sln) was not found.");
    });

    private static readonly Lazy<ValidatorSchema> EnvelopeV1 = new(() => Load("envelope.v1.schema.json"));

    private static readonly Lazy<IReadOnlyDictionary<string, ValidatorSchema>> PayloadsV1 = new(() =>
        EventTypes.ToDictionary(eventType => eventType, eventType => Load(PayloadSchemaFile(eventType))));

    public static string PayloadSchemaFile(string eventType) => Path.Combine("inventory", $"{eventType}.v1.schema.json");

    public static string SampleFile(string eventType) => Path.Combine("inventory", "samples", $"{eventType}.v1.json");

    public static string PathOf(string relativePath) => Path.Combine(SchemaRoot.Value, relativePath);

    public static string ReadText(string relativePath) => File.ReadAllText(PathOf(relativePath));

    public static bool IsValidEnvelope(JsonElement message) => EnvelopeV1.Value.Validate(message, ValidationLevel.Flag).IsValid;

    public static bool IsValidPayload(string eventType, JsonElement payload) => PayloadsV1.Value[eventType].Validate(payload, ValidationLevel.Flag).IsValid;

    /// <summary>Both validation steps of an Inventory v1 message, by the payload schema of its own event type.</summary>
    public static void AssertValid(string envelopeJson)
    {
        using var document = JsonDocument.Parse(envelopeJson);
        var root = document.RootElement;
        Assert.True(IsValidEnvelope(root), "The envelope does not match envelope.v1.schema.json.");
        var eventType = root.GetProperty("eventType").GetString()!;
        Assert.Contains(eventType, EventTypes);
        Assert.True(IsValidPayload(eventType, root.GetProperty("payload")), $"The payload does not match {eventType}.v1.schema.json.");
    }

    private static ValidatorSchema Load(string relativePath) =>
        ValidatorSchema.FromFile(PathOf(relativePath), new ValidatorSchema.Options(alwaysAssertFormat: true));
}
