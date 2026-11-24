namespace EnterpriseCommerce.Cart.Application.Common;

/// <summary>
/// The Cart database could not complete an operation (unreachable, timed out, or deadlocked beyond the retries).
/// Nothing is reported as done: the API answers <c>503</c>, and a caller may retry.
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
