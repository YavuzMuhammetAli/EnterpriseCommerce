namespace EnterpriseCommerce.Inventory.Infrastructure.ServiceClients;

/// <summary>
/// Inventory's own service client at Identity (<c>Inventory:ServiceClient</c>): the client credentials grant with
/// <c>private_key_jwt</c> (ADR-025 section 2, ADR-027 section 2). One client, one key, a token per scope: one for Tenant
/// &amp; Merchant's membership verification and one for Catalog's variant verification. The private key is read from a
/// file outside the repository.
/// </summary>
internal sealed class ServiceClientOptions
{
    public const string SectionName = "Inventory:ServiceClient";

    /// <summary>Identity's token endpoint.</summary>
    public Uri? TokenEndpoint { get; set; }

    /// <summary>The audience of the client assertion: the Identity issuer. Defaults to <c>Inventory:Authentication:Issuer</c>.</summary>
    public Uri? Issuer { get; set; }

    public string ClientId { get; set; } = "svc-inventory";

    /// <summary>A PEM file with the client's RSA private key, from a protected store. Never committed.</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The <c>kid</c> of the key in the client's registered JWKS, if it has one.</summary>
    public string? KeyId { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Whether any value is set. A partial configuration is an error, not a disabled client.</summary>
    public bool IsSpecified => TokenEndpoint is not null || !string.IsNullOrWhiteSpace(PrivateKeyPath);

    public IEnumerable<string> Validate(bool isDevelopment)
    {
        if (!IsSpecified)
        {
            if (!isDevelopment)
            {
                yield return $"{SectionName} is required: Inventory calls Tenant & Merchant and Catalog as a service client.";
            }

            yield break;
        }

        foreach (var (name, value) in new[] { ("TokenEndpoint", TokenEndpoint), ("Issuer", Issuer) })
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

/// <summary>
/// The base address of one internal API Inventory calls: Tenant &amp; Merchant's membership verification
/// (<c>Inventory:MembershipVerification</c>) or Catalog's variant verification (<c>Inventory:CatalogVerification</c>).
/// </summary>
internal abstract class ServiceEndpointOptions
{
    /// <summary>The service's internal base address.</summary>
    public Uri? BaseAddress { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    protected abstract string SectionName { get; }

    /// <summary>Whether the endpoint is configured. Without it, the calls fail closed with <c>503</c>.</summary>
    public bool IsSpecified => BaseAddress is not null;

    public IEnumerable<string> Validate(bool isDevelopment, bool serviceClientSpecified)
    {
        if (!IsSpecified)
        {
            if (!isDevelopment)
            {
                yield return $"{SectionName}:BaseAddress is required outside Development.";
            }

            yield break;
        }

        if (BaseAddress is not { IsAbsoluteUri: true } || (!isDevelopment && BaseAddress.Scheme != Uri.UriSchemeHttps))
        {
            yield return $"{SectionName}:BaseAddress must be an absolute {(isDevelopment ? string.Empty : "https ")}URI.";
        }

        if (!serviceClientSpecified)
        {
            yield return $"{SectionName} needs {ServiceClientOptions.SectionName}: the call is authenticated with a service token.";
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromSeconds(30))
        {
            yield return $"{SectionName}:Timeout must be between 0 and 30 seconds.";
        }
    }
}

/// <summary>Tenant &amp; Merchant's internal membership verification (ADR-025).</summary>
internal sealed class MembershipVerificationOptions : ServiceEndpointOptions
{
    public const string Section = "Inventory:MembershipVerification";

    /// <summary>The scope of Tenant &amp; Merchant's verification API (TENANT_MERCHANT_API_CONTRACT.md section 3.3).</summary>
    public const string Scope = "svc.tenant-merchant.membership-verification";

    protected override string SectionName => Section;
}

/// <summary>Catalog's internal variant verification (ADR-027).</summary>
internal sealed class CatalogVerificationOptions : ServiceEndpointOptions
{
    public const string Section = "Inventory:CatalogVerification";

    /// <summary>The scope of Catalog's variant verification (CATALOG_API_CONTRACT.md section 6a).</summary>
    public const string Scope = "svc.catalog.variant-verification";

    protected override string SectionName => Section;
}
