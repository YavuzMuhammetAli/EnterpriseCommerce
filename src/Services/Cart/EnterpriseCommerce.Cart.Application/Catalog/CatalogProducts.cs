using EnterpriseCommerce.Cart.Domain.Carts;

namespace EnterpriseCommerce.Cart.Application.Catalog;

/// <summary>
/// Catalog's public product read, as Cart uses it (ADR-029 section 5): the source of every product snapshot. Catalog
/// owns products, variants and prices; Cart only copies what it shows.
/// </summary>
public interface ICatalogProductReader
{
    /// <summary>
    /// The product as Catalog publishes it now, or <c>null</c> when Catalog has no published product with that ID
    /// (unknown, draft and archived are not told apart).
    /// </summary>
    /// <exception cref="CatalogUnavailableException">Catalog gave no usable answer. Nothing may be written.</exception>
    Task<CatalogProduct?> ReadAsync(Guid productId, string? correlationId, CancellationToken cancellationToken);
}

/// <summary>A published product: its merchant and the snapshot of each active variant, by variant ID.</summary>
public sealed record CatalogProduct(Guid ProductId, Guid TenantId, IReadOnlyDictionary<Guid, ProductSnapshot> ActiveVariants);

/// <summary>
/// Catalog gave no usable answer: not configured, a timeout, a transport error, an unexpected status or an unexpected
/// body. The command answers <c>503 catalog-unavailable</c> and writes nothing; it is never turned into "not found".
/// </summary>
public sealed class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException()
    {
    }

    public CatalogUnavailableException(string message)
        : base(message)
    {
    }

    public CatalogUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
