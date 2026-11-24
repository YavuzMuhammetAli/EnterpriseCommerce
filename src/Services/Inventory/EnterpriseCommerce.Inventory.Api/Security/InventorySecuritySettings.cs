using EnterpriseCommerce.BuildingBlocks.Authentication;

namespace EnterpriseCommerce.Inventory.Api.Security;

/// <summary>
/// Access token validation (ADR-017 section 4): tokens are validated locally against Identity's published keys.
/// Configuration section <c>Inventory:Authentication</c>.
/// </summary>
internal sealed class InventoryAuthenticationSettings
{
    public const string SectionName = "Inventory:Authentication";
    public const string DefaultServiceAudience = "inventory-api";
    public const string DefaultUserAudience = "ec-merchant";

    /// <summary>Identity's public issuer URI, matched exactly against <c>iss</c>. Required.</summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// Optional internal address of Identity's OpenID Connect metadata. Defaults to the issuer's
    /// <c>.well-known/openid-configuration</c>. The issuer in tokens is still the public issuer.
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>The audience of service tokens issued for this service.</summary>
    public string ServiceAudience { get; set; } = DefaultServiceAudience;

    /// <summary>The front-end audiences whose user tokens may call the merchant API (ADR-017 section 3). Defaults to the merchant portal.</summary>
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

        if (string.IsNullOrWhiteSpace(ServiceAudience))
        {
            yield return $"{SectionName}:ServiceAudience is required.";
        }

        if (EffectiveUserAudiences.Any(audience => string.IsNullOrWhiteSpace(audience) || audience == ServiceAudience))
        {
            yield return $"{SectionName}:UserAudiences must be non-empty and distinct from the service audience.";
        }
    }
}

/// <summary>
/// The internal reservation API for checkout orchestration (ADR-026 sections 6 and 10, INVENTORY_API_CONTRACT.md
/// section 7). Configuration section <c>Inventory:Reservations</c>; the lifetimes are validated by the Infrastructure.
/// </summary>
internal sealed class ReservationClientSettings
{
    public const string SectionName = "Inventory:Reservations";

    /// <summary>The scope a service token needs for the reservation API.</summary>
    public const string Scope = "svc.inventory.reservations";

    /// <summary>
    /// The <c>svc-</c> clients allowed to reserve stock. Empty by default, which denies every caller: Order, the intended
    /// caller, does not exist yet.
    /// </summary>
    public string[] AllowedClientIds { get; set; } = [];

    public IEnumerable<string> Validate()
    {
        foreach (var clientId in AllowedClientIds)
        {
            if (clientId is null
                || !clientId.StartsWith(CallerClaims.ServiceClientPrefix, StringComparison.Ordinal)
                || clientId.Length <= CallerClaims.ServiceClientPrefix.Length)
            {
                yield return $"{SectionName}:AllowedClientIds may contain only '{CallerClaims.ServiceClientPrefix}' service clients.";
            }
        }
    }
}
