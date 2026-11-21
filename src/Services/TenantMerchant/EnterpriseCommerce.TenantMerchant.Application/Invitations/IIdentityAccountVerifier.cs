namespace EnterpriseCommerce.TenantMerchant.Application.Invitations;

/// <summary>Identity's answer to "is this the verified email address of this active account?" (ADR-021).</summary>
public enum IdentityEmailCheck
{
    /// <summary>The account is active and its confirmed email is the address.</summary>
    Match,

    /// <summary>The account's confirmed email is a different address.</summary>
    Mismatch,

    /// <summary>The account's email is not confirmed.</summary>
    Unverified,

    /// <summary>The account is not active.</summary>
    Inactive,

    /// <summary>Identity knows no account with this subject.</summary>
    UnknownAccount,
}

/// <summary>
/// Asks Identity, the owner of accounts, whether an address is the verified email of an account. Tenant &amp; Merchant
/// keeps no copy of account data: it sends the invited address and receives only a verdict, never the account's email.
/// </summary>
public interface IIdentityAccountVerifier
{
    /// <param name="subjectId">The authenticated caller's <c>sub</c>.</param>
    /// <param name="email">The invited address.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="IdentityUnavailableException">Identity could not give a verdict. Callers fail closed.</exception>
    Task<IdentityEmailCheck> CheckEmailAsync(string subjectId, string email, CancellationToken cancellationToken);
}

/// <summary>
/// Identity could not be asked or gave no usable answer (not configured, unreachable, timed out, refused the service
/// token or answered something unexpected). Nothing is changed: the API answers <c>503</c>.
/// </summary>
public sealed class IdentityUnavailableException : Exception
{
    public IdentityUnavailableException()
    {
    }

    public IdentityUnavailableException(string message)
        : base(message)
    {
    }

    public IdentityUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
