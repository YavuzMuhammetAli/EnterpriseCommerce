using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;

namespace EnterpriseCommerce.TenantMerchant.Domain.Invitations;

/// <summary>
/// The email address an invitation is for. The rules are the same as Identity's account email rules (a plain address,
/// at most 256 characters, a dotted host, no control characters), and so is the normalized form (upper invariant), so
/// that "one pending invitation per address" and Identity's verified-email check agree on what one address is.
/// </summary>
/// <remarks>
/// The rules are a copy, not a reference: a service never references another service's projects. The value is personal
/// data. It is stored on the invitation only, and never logged, audited or put in messages.
/// </remarks>
public sealed record InviteeEmail
{
    public const int MaxLength = 256;

    private InviteeEmail(string value, string normalized)
    {
        Value = value;
        Normalized = normalized;
    }

    /// <summary>The address as entered, trimmed.</summary>
    public string Value { get; }

    /// <summary>The upper-invariant address used for uniqueness.</summary>
    public string Normalized { get; }

    public static bool TryCreate(string? input, [NotNullWhen(true)] out InviteeEmail? email)
    {
        email = null;
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

        email = new InviteeEmail(address.Address, address.Address.ToUpperInvariant());
        return true;
    }

    /// <summary>Never renders the address, so that it cannot reach a log through string formatting.</summary>
    public override string ToString() => "[invitee email]";
}
