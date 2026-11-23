using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// The Catalog MongoDB database. The connection string comes from <c>ConnectionStrings:CatalogDatabase</c> (an
/// environment variable or user secrets, never committed); the rest from <c>Catalog:Database</c>.
/// </summary>
internal sealed class CatalogDatabaseOptions
{
    public const string SectionName = "Catalog:Database";
    public const string ConnectionStringName = "CatalogDatabase";

    /// <summary>A MongoDB connection string (<c>mongodb://</c> or <c>mongodb+srv://</c>). Required.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>The service database. One database per service (ADR-003).</summary>
    public string DatabaseName { get; set; } = "catalog";

    /// <summary>How long a request waits for a usable server before failing with <c>503</c>.</summary>
    public TimeSpan ServerSelectionTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The server time limit of every query (<c>maxTimeMS</c>), so no query runs unbounded.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            yield return $"ConnectionStrings:{ConnectionStringName} is required.";
        }
        else if (!IsParsable(ConnectionString))
        {
            yield return $"ConnectionStrings:{ConnectionStringName} is not a valid MongoDB connection string.";
        }

        if (DatabaseName is not { Length: > 0 and <= 63 } || !DatabaseName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            yield return $"{SectionName}:DatabaseName must be 1 to 63 characters of letters, digits, '_' or '-'.";
        }

        if (ServerSelectionTimeout <= TimeSpan.Zero || ServerSelectionTimeout > TimeSpan.FromSeconds(60))
        {
            yield return $"{SectionName}:ServerSelectionTimeout must be greater than 0 and at most 60 seconds.";
        }

        if (QueryTimeout <= TimeSpan.Zero || QueryTimeout > TimeSpan.FromSeconds(60))
        {
            yield return $"{SectionName}:QueryTimeout must be greater than 0 and at most 60 seconds.";
        }
    }

    private static bool IsParsable(string connectionString)
    {
        try
        {
            _ = MongoUrl.Create(connectionString);
            return true;
        }
        catch (MongoConfigurationException)
        {
            return false;
        }
    }
}
