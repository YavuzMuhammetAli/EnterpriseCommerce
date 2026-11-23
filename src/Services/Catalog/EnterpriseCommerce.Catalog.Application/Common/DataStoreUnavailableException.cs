namespace EnterpriseCommerce.Catalog.Application.Common;

/// <summary>
/// The Catalog database (MongoDB) could not complete an operation (unreachable, timed out, refused by an unfinished index setup or otherwise
/// failed). Nothing is reported as done: the API answers <c>503</c>, and a caller may retry, with the same
/// <c>Idempotency-Key</c> where one applies.
/// </summary>
public sealed class DataStoreUnavailableException : Exception
{
    public DataStoreUnavailableException()
    {
    }

    public DataStoreUnavailableException(string message)
        : base(message)
    {
    }

    public DataStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
