using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace EnterpriseCommerce.TenantMerchant.IntegrationTests.Infrastructure;

/// <summary>One outbox row as stored.</summary>
public sealed record StoredOutboxRow(
    long Sequence,
    Guid EventId,
    string Topic,
    string MessageKey,
    string EventType,
    int EventVersion,
    string Envelope,
    DateTimeOffset OccurredAt,
    DateTimeOffset? PublishedAt,
    int Attempts,
    DateTimeOffset? LastAttemptAt,
    string? LastError);

/// <summary>
/// Test-only access to <c>OutboxMessages</c>: synthetic rows written directly to the table (S4 has no second event
/// version, PHASE_3_S4_EVENT_FLOW.md section 8), and reads of what the application or the relay wrote.
/// </summary>
public static class OutboxRows
{
    public const string DefaultTopic = "tenant-merchant.events";

    /// <summary>Inserts a pending synthetic row with a schema-valid envelope and returns its sequence.</summary>
    public static Task<long> InsertAsync(
        SqlServerTestDatabase database,
        Guid eventId,
        int eventVersion,
        string? messageKey = null,
        string topic = DefaultTopic,
        int[]? publishedVersions = null,
        string? envelope = null) =>
        InsertAsync(database.ConnectionString, eventId, eventVersion, messageKey, topic, publishedVersions, envelope);

    public static async Task<long> InsertAsync(
        string connectionString,
        Guid eventId,
        int eventVersion,
        string? messageKey = null,
        string topic = DefaultTopic,
        int[]? publishedVersions = null,
        string? envelope = null,
        SqlTransaction? transaction = null,
        SqlConnection? connection = null)
    {
        var key = messageKey ?? Guid.CreateVersion7().ToString("D");
        var occurredAt = DateTimeOffset.UtcNow;
        envelope ??= SyntheticEnvelope(eventId, eventVersion, publishedVersions ?? [eventVersion], key, occurredAt);

        var ownsConnection = connection is null;
        connection ??= new SqlConnection(connectionString);
        try
        {
            if (ownsConnection)
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO OutboxMessages (EventId, Topic, MessageKey, EventType, EventVersion, Envelope, OccurredAt)
                OUTPUT INSERTED.Sequence
                VALUES (@eventId, @topic, @key, N'SyntheticHappened', @version, @envelope, @occurredAt)
                """;
            command.Parameters.AddWithValue("@eventId", eventId);
            command.Parameters.AddWithValue("@topic", topic);
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@version", eventVersion);
            command.Parameters.AddWithValue("@envelope", envelope);
            command.Parameters.AddWithValue("@occurredAt", occurredAt);
            return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        }
        finally
        {
            if (ownsConnection)
            {
                await connection.DisposeAsync();
            }
        }
    }

    /// <summary>An envelope that validates against envelope.v1.schema.json, for a synthetic event type.</summary>
    public static string SyntheticEnvelope(Guid eventId, int eventVersion, int[] publishedVersions, string key, DateTimeOffset occurredAt) =>
        new JsonObject
        {
            ["eventId"] = eventId.ToString("D"),
            ["eventType"] = "SyntheticHappened",
            ["eventVersion"] = eventVersion,
            ["publishedVersions"] = new JsonArray([.. publishedVersions.Select(version => (JsonNode)version)]),
            ["producer"] = "tenant-merchant",
            ["aggregateId"] = key,
            ["tenantId"] = key,
            ["occurredAtUtc"] = occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["correlationId"] = "synthetic-correlation",
            ["causationId"] = "synthetic-correlation",
            ["payload"] = new JsonObject { ["version"] = eventVersion },
        }.ToJsonString();

    /// <summary>Every row, in sequence order, optionally only those of one event type.</summary>
    public static async Task<IReadOnlyList<StoredOutboxRow>> ReadAsync(SqlServerTestDatabase database, string? eventType = null)
    {
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sequence, EventId, Topic, MessageKey, EventType, EventVersion, Envelope, OccurredAt, PublishedAt, Attempts, LastAttemptAt, LastError
            FROM OutboxMessages
            WHERE @eventType IS NULL OR EventType = @eventType
            ORDER BY Sequence
            """;
        command.Parameters.Add(new SqlParameter("@eventType", System.Data.SqlDbType.VarChar, 128) { Value = (object?)eventType ?? DBNull.Value });

        var rows = new List<StoredOutboxRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new StoredOutboxRow(
                reader.GetInt64(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                reader.GetDateTimeOffset(7),
                reader.IsDBNull(8) ? null : reader.GetDateTimeOffset(8),
                reader.GetInt32(9),
                reader.IsDBNull(10) ? null : reader.GetDateTimeOffset(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return rows;
    }

    /// <summary>The <c>TenantCreated</c> rows whose envelope names the tenant.</summary>
    public static async Task<IReadOnlyList<StoredOutboxRow>> TenantCreatedAsync(SqlServerTestDatabase database, Guid tenantId) =>
        [.. (await ReadAsync(database, "TenantCreated")).Where(row => row.MessageKey == tenantId.ToString("D"))];
}
