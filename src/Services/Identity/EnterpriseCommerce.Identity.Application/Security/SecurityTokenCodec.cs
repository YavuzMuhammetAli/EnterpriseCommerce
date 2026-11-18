using System.Security.Cryptography;
using System.Text;

namespace EnterpriseCommerce.Identity.Application.Security;

/// <summary>A newly generated account security token. <see cref="RawToken"/> exists only in memory.</summary>
public sealed record GeneratedSecurityToken(string RawToken, string TokenHash)
{
    /// <summary>Never renders the raw token, so that it cannot leak through logging or debugging output.</summary>
    public override string ToString() => $"GeneratedSecurityToken {{ TokenHash = {TokenHash} }}";
}

/// <summary>
/// Generates 256-bit random account security tokens and computes the SHA-256 hash that is stored
/// in their place (ADR-018 section 1).
/// </summary>
public static class SecurityTokenCodec
{
    private const int TokenSizeInBytes = 32;

    /// <summary>Length of the URL-safe Base64 encoding of 32 bytes without padding.</summary>
    public const int RawTokenLength = 43;

    /// <summary>Generates a URL-safe token from a cryptographically secure random source.</summary>
    public static GeneratedSecurityToken Generate()
    {
        var raw = Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenSizeInBytes));
        return new GeneratedSecurityToken(raw, Hash(raw));
    }

    /// <summary>Returns the lowercase hexadecimal SHA-256 hash of the token.</summary>
    public static string Hash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawToken);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
    }

    /// <summary>
    /// Returns whether the input has the shape of a generated token. Used to reject malformed input
    /// before any database lookup. It is not a validity check.
    /// </summary>
    public static bool HasValidFormat(string? rawToken) =>
        rawToken is { Length: RawTokenLength } && rawToken.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
