using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// The service's MongoDB client, database and collections (one singleton per host; the client pools connections).
/// </summary>
internal sealed class CatalogMongoContext
{
    public const string ProductsCollection = "products";
    public const string CategoriesCollection = "categories";
    public const string BrandsCollection = "brands";

    public CatalogMongoContext(CatalogDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        MongoSerialization.EnsureConfigured();

        var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
        settings.ServerSelectionTimeout = options.ServerSelectionTimeout;
        settings.ConnectTimeout = options.ServerSelectionTimeout;
        settings.ApplicationName = "enterprisecommerce-catalog";

        Client = new MongoClient(settings);
        Database = Client.GetDatabase(options.DatabaseName);
        QueryTimeout = options.QueryTimeout;
        Products = Database.GetCollection<ProductDocument>(ProductsCollection);
        Categories = Database.GetCollection<CategoryDocument>(CategoriesCollection);
        Brands = Database.GetCollection<BrandDocument>(BrandsCollection);
    }

    public IMongoClient Client { get; }

    public IMongoDatabase Database { get; }

    /// <summary>The server time limit of every query.</summary>
    public TimeSpan QueryTimeout { get; }

    public IMongoCollection<ProductDocument> Products { get; }

    public IMongoCollection<CategoryDocument> Categories { get; }

    public IMongoCollection<BrandDocument> Brands { get; }

    /// <summary>Whether the server answers a <c>ping</c>.</summary>
    public async Task PingAsync(CancellationToken cancellationToken) =>
        await Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
}

/// <summary>
/// Process-wide BSON configuration, applied once: the driver's serializer registry is global, and a test process hosts
/// several applications, so registration must be idempotent.
/// </summary>
internal static class MongoSerialization
{
    private static readonly Lock Gate = new();
    private static bool _configured;

    public static void EnsureConfigured()
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }

            // Standard (subtype 4) GUIDs for every Guid, Guid? and List<Guid> member.
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));

            // camelCase element names for this assembly's documents only.
            ConventionRegistry.Register(
                "EnterpriseCommerce.Catalog",
                new ConventionPack { new CamelCaseElementNameConvention() },
                type => type.Namespace?.StartsWith("EnterpriseCommerce.Catalog.Infrastructure", StringComparison.Ordinal) == true);
            _configured = true;
        }
    }
}
