namespace EnterpriseCommerce.Identity.Domain.Accounts;

/// <summary>
/// Why an administrator changed an account's status (PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.6.6). A bounded code,
/// stored with the account and written to the security audit log. Free text is never accepted.
/// </summary>
public enum AccountStatusReason
{
    Administrative = 0,
    PolicyViolation = 1,
    SecurityConcern = 2,
    UserRequest = 3,
}

/// <summary>The rules of the account lifecycle operations (disable and reactivate).</summary>
public static class AccountLifecycle
{
    /// <summary>Maximum length of the opaque reference to the administrator who requested a change.</summary>
    public const int ActorReferenceMaxLength = 128;

    /// <summary>Only the two administrative transitions exist: <c>Active → Disabled</c> and <c>Disabled → Active</c>.</summary>
    public static bool CanTransition(AccountStatus from, AccountStatus to) =>
        (from, to) is (AccountStatus.Active, AccountStatus.Disabled) or (AccountStatus.Disabled, AccountStatus.Active);

    /// <summary>
    /// An actor reference is an opaque identifier issued by Admin: 1 to 128 characters of
    /// <c>[A-Za-z0-9._:-]</c>. It identifies the administrator for the audit trail only and never authorizes anything.
    /// </summary>
    public static bool IsValidActorReference(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= ActorReferenceMaxLength
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');

    /// <summary>Whether <paramref name="reason"/> is one of the defined reason codes.</summary>
    public static bool IsDefined(AccountStatusReason reason) => Enum.IsDefined(reason);
}
