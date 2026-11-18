namespace EnterpriseCommerce.Identity.Domain.Accounts;

/// <summary>The account facts that decide whether a user may sign in or keep a session.</summary>
public sealed record AccountSignInState(AccountStatus Status, bool EmailConfirmed)
{
    /// <summary>Only active accounts with a confirmed email may sign in or refresh (ADR-017 section 9).</summary>
    public bool CanSignIn => Status == AccountStatus.Active && EmailConfirmed;
}
