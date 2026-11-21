using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using EnterpriseCommerce.TenantMerchant.Domain.Invitations;

namespace EnterpriseCommerce.TenantMerchant.Application.Invitations;

/// <summary>
/// The single-use acceptance token of an invitation (ADR-021): 256 bits from a cryptographically secure generator,
/// base64url-encoded behind the recognizable prefix <c>ecmi_</c> (so that secret scanners and log reviews can spot one).
/// Only its SHA-256 hash is stored; the raw token exists in memory until it is returned once to the inviter.
/// </summary>
public static class InvitationToken
{
    public const string Prefix = "ecmi_";

    private const int SecretBytes = 32;

    /// <summary>The length of a token: the prefix and 43 base64url characters.</summary>
    public static readonly int Length = Prefix.Length + Base64Url.GetEncodedLength(SecretBytes);

    /// <summary>Creates a new token and its hash.</summary>
    public static (string Token, InvitationTokenHash Hash) Generate()
    {
        Span<byte> secret = stackalloc byte[SecretBytes];
        RandomNumberGenerator.Fill(secret);
        var token = Prefix + Base64Url.EncodeToString(secret);
        CryptographicOperations.ZeroMemory(secret);
        return (token, Hash(token));
    }

    /// <summary>
    /// Hashes a presented token if it has the token format. A value of another format cannot be a token, and is
    /// answered exactly like an unknown token.
    /// </summary>
    public static bool TryHash(string? token, [NotNullWhen(true)] out InvitationTokenHash? hash)
    {
        hash = token is not null
            && token.Length == Length
            && token.StartsWith(Prefix, StringComparison.Ordinal)
            && IsBase64UrlAlphabet(token.AsSpan(Prefix.Length))
                ? Hash(token)
                : null;
        return hash is not null;
    }

    // Strict: Base64Url.IsValid also accepts padding and whitespace, which a generated token never contains.
    private static bool IsBase64UrlAlphabet(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static InvitationTokenHash Hash(string token) =>
        InvitationTokenHash.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
