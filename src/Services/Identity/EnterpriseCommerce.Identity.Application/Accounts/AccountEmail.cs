using System.Net.Mail;

namespace EnterpriseCommerce.Identity.Application.Accounts;

/// <summary>Server-side validation and normalization of account email addresses.</summary>
public static class AccountEmail
{
    public const int MaxLength = 256;

    /// <summary>
    /// Accepts a plain address only (no display name, no surrounding text). The normalized form is the upper-invariant
    /// address, the same as ASP.NET Core Identity's default lookup normalizer, so uniqueness checks agree.
    /// </summary>
    public static bool TryNormalize(string? input, out string email, out string normalized)
    {
        email = string.Empty;
        normalized = string.Empty;

        var candidate = input?.Trim();
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength || candidate.Any(char.IsControl))
        {
            return false;
        }

        if (!MailAddress.TryCreate(candidate, out var address)
            || !string.Equals(address.Address, candidate, StringComparison.Ordinal)
            || !address.Host.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        email = address.Address;
        normalized = email.ToUpperInvariant();
        return true;
    }
}

/// <summary>The password policy of ASP.NET Core Identity, evaluated before any account lookup.</summary>
public interface IAccountPasswordPolicy
{
    /// <summary>Returns the policy violations as stable codes. An empty list means the password is acceptable.</summary>
    Task<IReadOnlyList<string>> ValidateAsync(string password, CancellationToken cancellationToken);
}

/// <summary>
/// Hashes a new password with the configured ASP.NET Core Identity password hasher. Flows hash before their
/// authoritative transaction, so that no database lock is held while the deliberately slow hash is computed.
/// </summary>
public interface IAccountPasswordHasher
{
    string Hash(string password);
}
