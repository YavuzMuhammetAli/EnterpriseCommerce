namespace EnterpriseCommerce.Identity.Application.Security;

/// <summary>
/// The authoritative security state could not be read or committed, for example because the
/// database is unavailable or deadlock retries were exhausted. Callers must fail closed:
/// no token is issued and no success is reported (ADR-017 section 8). The API maps it to 503.
/// </summary>
public sealed class SecurityStateUnavailableException : Exception
{
    public SecurityStateUnavailableException()
        : this("The authoritative security state is unavailable.")
    {
    }

    public SecurityStateUnavailableException(string message)
        : base(message)
    {
    }

    public SecurityStateUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
