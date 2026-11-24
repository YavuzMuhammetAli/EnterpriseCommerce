namespace EnterpriseCommerce.Cart.Api.Security;

/// <summary>
/// Access token validation (ADR-017 section 4): tokens are validated locally against Identity's published keys.
/// Configuration section <c>Cart:Authentication</c>.
/// </summary>
internal sealed class CartAuthenticationSettings
{
    public const string SectionName = "Cart:Authentication";

    /// <summary>The storefront front end's audience (ADR-017 section 3): Cart serves storefront customers (ADR-029 section 1).</summary>
    public const string DefaultUserAudience = "ec-storefront";

    /// <summary>Identity's public issuer URI, matched exactly against <c>iss</c>. Required.</summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Optional internal address of Identity's OpenID Connect metadata. Defaults to the issuer's
    /// <c>.well-known/openid-configuration</c>. The issuer in tokens is still the public issuer.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>The front-end audiences whose user tokens are customers. Defaults to the storefront.</summary>
    public string[]? UserAudiences { get; set; }

    public IReadOnlyList<string> EffectiveUserAudiences => UserAudiences is { Length: > 0 } audiences ? audiences : [DefaultUserAudience];

    public IEnumerable<string> Validate(bool isDevelopment)
    {
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer))
        {
            yield return $"{SectionName}:Issuer must be an absolute URI.";
        }
        else if (!isDevelopment && issuer.Scheme != Uri.UriSchemeHttps)
        {
            yield return $"{SectionName}:Issuer must use HTTPS outside Development.";
        }

        if (MetadataAddress is { Length: > 0 } && !Uri.TryCreate(MetadataAddress, UriKind.Absolute, out _))
        {
            yield return $"{SectionName}:MetadataAddress must be an absolute URI.";
        }

        if (EffectiveUserAudiences.Any(string.IsNullOrWhiteSpace))
        {
            yield return $"{SectionName}:UserAudiences must not contain an empty audience.";
        }
    }
}
