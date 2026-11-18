namespace EnterpriseCommerce.Identity.Domain.Security;

/// <summary>
/// What a single-use account security token may be used for (ADR-018 section 1).
/// A token is only ever consumed for the purpose it was issued for.
/// </summary>
public enum SecurityTokenPurpose
{
    EmailConfirmation = 1,
    PasswordReset = 2,
}
