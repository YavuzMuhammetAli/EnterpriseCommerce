namespace EnterpriseCommerce.Catalog.Application.Authorization;

/// <summary>
/// Tenant &amp; Merchant's membership verdict could not be obtained: no verification client is configured, the call
/// failed, timed out or answered something unexpected, or no service token was issued (ADR-025 section 2). The request
/// is refused without reading or writing anything, and the API answers <c>503</c>.
/// </summary>
public sealed class MembershipUnavailableException : Exception
{
    public MembershipUnavailableException()
    {
    }

    public MembershipUnavailableException(string message)
        : base(message)
    {
    }

    public MembershipUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
