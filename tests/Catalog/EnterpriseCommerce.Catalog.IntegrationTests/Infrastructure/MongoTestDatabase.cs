using MongoDB.Bson;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway Catalog database on a real MongoDB, dropped afterwards.
/// </summary>
/// <remarks>
/// <para>The server comes from <c>EC_TEST_MONGODB</c> (a connection string; the database is chosen here), for example
/// with Compose (<c>MONGO_PORT=27018 docker compose --profile mongodb up -d --wait</c>):
/// <c>mongodb://localdev:&lt;MONGO_ROOT_PASSWORD from deploy/docker/.env&gt;@127.0.0.1:27018/?authSource=admin</c>.</para>
/// <para>
/// Without it, MongoDB-backed tests are skipped with a reason. CI sets <c>EC_REQUIRE_INFRA_TESTS=true</c>, which turns
/// that skip into a failure, so infrastructure tests can never silently disappear from CI.
/// </para>
/// </remarks>
public sealed class MongoTestDatabase : IAsyncDisposable
{
    public const string ServerVariable = "EC_TEST_MONGODB";
    public const string RequireVariable = "EC_REQUIRE_INFRA_TESTS";

    private readonly string? _connectionString = Environment.GetEnvironmentVariable(ServerVariable);
    private MongoClient? _client;

    public string DatabaseName { get; } = $"ec_catalog_test_{Guid.NewGuid():N}";

    public string ConnectionString => _connectionString ?? string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    /// <summary>The test database, for direct inspection (raw documents, indexes).</summary>
    public IMongoDatabase Database => (_client ??= new MongoClient(ConnectionString)).GetDatabase(DatabaseName);

    /// <summary>Skips the calling test when no MongoDB is configured, or fails it when infrastructure tests are required.</summary>
    public void RequireAvailable()
    {
        if (IsConfigured)
        {
            return;
        }

        var message = $"MongoDB integration test: set {ServerVariable} to a MongoDB connection string (see MongoTestDatabase).";
        if (string.Equals(Environment.GetEnvironmentVariable(RequireVariable), "true", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"{RequireVariable} is set but {ServerVariable} is not. {message}");
        }

        Assert.Skip(message);
    }

    /// <summary>The names of a collection's indexes.</summary>
    public async Task<IReadOnlyList<string>> IndexNamesAsync(string collection)
    {
        using var cursor = await Database.GetCollection<BsonDocument>(collection).Indexes.ListAsync(TestContext.Current.CancellationToken);
        var indexes = await cursor.ToListAsync(TestContext.Current.CancellationToken);
        return [.. indexes.Select(index => index["name"].AsString)];
    }

    /// <summary>
    /// Drops the database whenever a server is configured: the host under test creates it (indexes, documents) even
    /// when the test itself never touched <see cref="Database"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsConfigured)
        {
            return;
        }

        _client ??= new MongoClient(ConnectionString);
        await _client.DropDatabaseAsync(DatabaseName);
        _client.Dispose();
    }
}
