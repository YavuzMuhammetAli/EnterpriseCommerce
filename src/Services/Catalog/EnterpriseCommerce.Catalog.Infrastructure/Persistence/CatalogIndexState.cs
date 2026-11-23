using EnterpriseCommerce.Catalog.Application.Common;

namespace EnterpriseCommerce.Catalog.Infrastructure.Persistence;

/// <summary>The index setup of this host: pending until every index exists, then ready, or failed for good.</summary>
internal enum IndexSetupStatus
{
    Pending,
    Ready,
    Failed,
}

/// <summary>
/// Gates writes on the index setup (ADR-024 section 10): until every unique index exists, no write is accepted, so
/// uniqueness is never unprotected. Reads need no gate. A singleton per host.
/// </summary>
internal sealed class CatalogIndexState
{
    private volatile IndexSetupStatus _status = IndexSetupStatus.Pending;

    public IndexSetupStatus Status => _status;

    public void MarkReady() => _status = IndexSetupStatus.Ready;

    public void MarkFailed() => _status = IndexSetupStatus.Failed;

    /// <summary>Throws <see cref="DataStoreUnavailableException"/> (answered <c>503</c>) until the indexes are ready.</summary>
    public void EnsureWritable()
    {
        if (_status != IndexSetupStatus.Ready)
        {
            throw new DataStoreUnavailableException("The Catalog indexes are not ready; writes are refused.");
        }
    }
}
