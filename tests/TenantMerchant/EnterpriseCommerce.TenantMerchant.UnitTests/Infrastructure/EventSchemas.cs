using System.Text.Json;
using Corvus.Json;
using ValidatorSchema = Corvus.Json.Validator.JsonSchema;

namespace EnterpriseCommerce.TenantMerchant.UnitTests.Infrastructure;

/// <summary>
/// The committed event schemas under <c>docs/events/schemas</c>, compiled once, with <c>format</c> asserted. Messages
/// are validated in the two steps of docs/events/README.md: the envelope, then the payload by its own schema.
/// </summary>
internal static class EventSchemas
{
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
    private static readonly Lazy<ValidatorSchema> TenantCreatedV1 = new(() => Load(Path.Combine("tenant-merchant", "TenantCreated.v1.schema.json")));

    public static string PathOf(string relativePath) => Path.Combine(SchemaRoot.Value, relativePath);

    public static string ReadText(string relativePath) => File.ReadAllText(PathOf(relativePath));

    public static bool IsValidEnvelope(JsonElement message) => EnvelopeV1.Value.Validate(message, ValidationLevel.Flag).IsValid;

    public static bool IsValidTenantCreatedV1Payload(JsonElement payload) => TenantCreatedV1.Value.Validate(payload, ValidationLevel.Flag).IsValid;

    /// <summary>Both validation steps of a <c>TenantCreated</c> v1 message.</summary>
    public static void AssertValidTenantCreatedV1(string envelopeJson)
    {
        using var document = JsonDocument.Parse(envelopeJson);
        Assert.True(IsValidEnvelope(document.RootElement), "The envelope does not match envelope.v1.schema.json.");
        Assert.True(IsValidTenantCreatedV1Payload(document.RootElement.GetProperty("payload")), "The payload does not match TenantCreated.v1.schema.json.");
    }

    private static ValidatorSchema Load(string relativePath) =>
        ValidatorSchema.FromFile(PathOf(relativePath), new ValidatorSchema.Options(alwaysAssertFormat: true));
}
