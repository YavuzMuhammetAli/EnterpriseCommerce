using EnterpriseCommerce.Catalog.Application.Common;
using MongoDB.Driver;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>
/// Error translation for every store operation (ADR-024 sections 10 and 11). A failed MongoDB operation becomes
/// <see cref="DataStoreUnavailableException"/> (answered <c>503</c>, nothing reported as done); a duplicate key is an
/// outcome, identified by the name of the rejecting index (the message, which contains stored values, is never logged).
/// </summary>
internal static class MongoOperations
{
    private static readonly string[] UniqueIndexNames =
    [
        "ux_products_tenant_slug",
        "ux_products_tenant_sku",
        "ux_products_tenant_creation",
        "ux_categories_slug",
        "ux_brands_tenant_slug",
    ];

    public static async Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (Exception exception) when (exception is MongoException or TimeoutException && exception is not MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey })
        {
            throw new DataStoreUnavailableException("The Catalog database could not complete the operation.", exception);
        }
    }

    /// <summary>
    /// The name of the unique index that rejected a duplicate key, or <c>null</c> if the error is no duplicate key. The
    /// driver does not expose the server's <c>keyPattern</c>, so the name is read from the server message
    /// (<c>... index: &lt;name&gt; dup key: ...</c>) and accepted only if it is one of this service's index names. The
    /// message itself, which contains the stored values, is never logged or returned.
    /// </summary>
    public static string? DuplicateKeyIndex(MongoWriteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.WriteError is not { Category: ServerErrorCategory.DuplicateKey } error)
        {
            return null;
        }

        return UniqueIndexNames.FirstOrDefault(name => error.Message.Contains($" index: {name} dup key", StringComparison.Ordinal)) ?? string.Empty;
    }

    /// <summary>Maps a duplicate key of a product write to its outcome.</summary>
    public static WriteOutcome ProductDuplicate(string index) =>
        index switch
        {
            "ux_products_tenant_sku" => WriteOutcome.SkuTaken,
            "ux_products_tenant_creation" => WriteOutcome.CreationKeyTaken,
            "ux_products_tenant_slug" => WriteOutcome.SlugTaken,
            _ => throw new InvalidOperationException("A product write was rejected by an unknown unique index."),
        };

    /// <summary>Maps a duplicate key of a category or brand write to its outcome.</summary>
    public static WriteOutcome SlugDuplicate(string index) =>
        index is "ux_categories_slug" or "ux_brands_tenant_slug"
            ? WriteOutcome.SlugTaken
            : throw new InvalidOperationException("A write was rejected by an unknown unique index.");

    public static FindOptions FindOptions(CatalogMongoContext context) => new() { MaxTime = context.QueryTimeout };

    public static CountOptions CountOptions(CatalogMongoContext context) => new() { MaxTime = context.QueryTimeout };
}
