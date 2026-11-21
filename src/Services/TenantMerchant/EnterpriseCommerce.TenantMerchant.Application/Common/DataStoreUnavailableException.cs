namespace EnterpriseCommerce.TenantMerchant.Application.Common;

/// <summary>
/// The Tenant &amp; Merchant database could not complete an operation (unreachable, timed out, deadlocked or otherwise
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
