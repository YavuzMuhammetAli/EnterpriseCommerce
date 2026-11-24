namespace EnterpriseCommerce.Inventory.Application.Common;

/// <summary>
/// The Inventory database could not complete an operation (unreachable, timed out, a lock wait exceeded, or deadlocked
/// beyond the retries). Nothing is reported as done: the API answers <c>503</c>, and a caller may retry, with the same
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
