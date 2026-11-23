namespace EnterpriseCommerce.Catalog.Infrastructure.Membership;

/// <summary>
/// The Tenant &amp; Merchant integration of merchant authorization (<c>Catalog:MembershipVerification</c>, ADR-025).
/// Catalog authenticates at Identity as the service client <see cref="ClientId"/> with <c>private_key_jwt</c>, then
/// calls Tenant &amp; Merchant's internal membership verification endpoint. The private key is read from a file outside
/// the repository.
/// </summary>
internal sealed class MembershipVerificationOptions
{
    public const string SectionName = "Catalog:MembershipVerification";

    /// <summary>The scope of Tenant &amp; Merchant's verification API (TENANT_MERCHANT_API_CONTRACT.md section 3.3).</summary>
    public const string Scope = "svc.tenant-merchant.membership-verification";

    /// <summary>Tenant &amp; Merchant's internal base address.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Identity's token endpoint (another service than <see cref="BaseAddress"/>, so it has no default).</summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>The audience of the client assertion: the Identity issuer. Defaults to <c>Catalog:Authentication:Issuer</c>.</summary>
    public Uri? Issuer { get; set; }

    public string ClientId { get; set; } = "svc-catalog";

    /// <summary>A PEM file with the client's RSA private key, from a protected store. Never committed.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The <c>kid</c> of the key in the client's registered JWKS, if it has one.</summary>
    public string? KeyId { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether any value is set. A partial configuration is an error, not a disabled integration.</summary>
    public bool IsSpecified => BaseAddress is not null || TokenEndpoint is not null || !string.IsNullOrWhiteSpace(PrivateKeyPath);

    /// <summary>The configuration errors. Outside Development the integration is required and must use https.</summary>
    public IEnumerable<string> Validate(bool isDevelopment)
    {
        if (!IsSpecified)
        {
            if (!isDevelopment)
            {
                yield return $"{SectionName} is required: every merchant request is authorized by Tenant & Merchant.";
            }

            yield break;
        }

        foreach (var (name, value) in new[] { ("BaseAddress", BaseAddress), ("TokenEndpoint", TokenEndpoint), ("Issuer", Issuer) })
        {
            if (value is not { IsAbsoluteUri: true } || (!isDevelopment && value.Scheme != Uri.UriSchemeHttps))
            {
                yield return $"{SectionName}:{name} must be an absolute {(isDevelopment ? string.Empty : "https ")}URI.";
            }
        }

        if (!ClientId.StartsWith("svc-", StringComparison.Ordinal) || ClientId.Length == "svc-".Length)
        {
            yield return $"{SectionName}:ClientId must be a service client (svc- prefix).";
        }

        if (string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            yield return $"{SectionName}:PrivateKeyPath is required.";
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromSeconds(30))
        {
            yield return $"{SectionName}:Timeout must be between 0 and 30 seconds.";
        }
    }
}
