namespace EnterpriseCommerce.Identity.Domain.Accounts;

/// <summary>
/// Whether an account may authenticate. Disablement is a dedicated status and is never
/// represented by lockout (ADR-017 section 9, ADR-018 section 2).
/// </summary>
public enum AccountStatus
{
    Active = 0,
    Disabled = 1,
}
