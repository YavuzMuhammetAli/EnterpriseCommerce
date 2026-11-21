namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>The kind of an OAuth client registered with Identity.</summary>
internal enum ClientKind
{
    /// <summary>A backend-for-frontend: authorization code + PKCE, refresh tokens, one front-end audience (ADR-016).</summary>
    Browser,

    /// <summary>A service client: client credentials only. Its client ID must start with <c>svc-</c> (ADR-017 section 3).</summary>
    Service,
}

/// <summary>A scope a service client may request, and the audience (called service) it maps to.</summary>
internal sealed class ServiceScopeRegistration
{
    public string Scope { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;
}

/// <summary>One OAuth client, registered from configuration (<c>Identity:Clients</c>).</summary>
internal sealed class ClientRegistration
{
    public const string ServiceClientPrefix = "svc-";

    public string ClientId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public ClientKind Kind { get; set; } = ClientKind.Browser;

    /// <summary>Browser clients: the front-end audience of their access tokens (ADR-017 section 3, Option B).</summary>
    public string? Audience { get; set; }

    public List<string> RedirectUris { get; set; } = [];

    public List<string> PostLogoutRedirectUris { get; set; } = [];

    /// <summary>Public keys for <c>private_key_jwt</c> client authentication, as a JWK set JSON document.</summary>
    public string? JsonWebKeySet { get; set; }

    /// <summary>Development only: a client secret from user secrets or the environment. Rejected in other environments.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Default access token lifetime of a browser (BFF) client (PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.4).</summary>
    public static readonly TimeSpan DefaultBrowserAccessTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Default access token lifetime of a service client (PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.4, finding I38).</summary>
    public static readonly TimeSpan DefaultServiceAccessTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Configured access token lifetime. When unset, the default of the client's kind applies
    /// (<see cref="EffectiveAccessTokenLifetime"/>).
    /// </summary>
    public TimeSpan? AccessTokenLifetime { get; set; }

    /// <summary>The access token lifetime Identity registers for this client.</summary>
    public TimeSpan EffectiveAccessTokenLifetime =>
        AccessTokenLifetime ?? (Kind == ClientKind.Service ? DefaultServiceAccessTokenLifetime : DefaultBrowserAccessTokenLifetime);

    /// <summary>Sliding (inactivity) lifetime of refresh tokens.</summary>
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Absolute session lifetime. No refresh token outlives it (ADR-017 section 5).</summary>
    public TimeSpan AbsoluteSessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Service clients: the scopes they may request and their audiences.</summary>
    public List<ServiceScopeRegistration> ServiceScopes { get; set; } = [];

    public IEnumerable<string> Validate(bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            yield return "A client has no ClientId.";
            yield break;
        }

        var isServiceId = ClientId.StartsWith(ServiceClientPrefix, StringComparison.Ordinal);
        if (Kind == ClientKind.Service && !isServiceId)
        {
            yield return $"Service client '{ClientId}' must use the '{ServiceClientPrefix}' prefix.";
        }

        if (Kind == ClientKind.Browser && isServiceId)
        {
            yield return $"The '{ServiceClientPrefix}' prefix is reserved for service clients ('{ClientId}').";
        }

        if (string.IsNullOrWhiteSpace(JsonWebKeySet) && string.IsNullOrWhiteSpace(ClientSecret))
        {
            yield return $"Client '{ClientId}' has no credential (JsonWebKeySet or, in Development, ClientSecret).";
        }

        if (!string.IsNullOrWhiteSpace(ClientSecret) && !isDevelopment)
        {
            yield return $"Client '{ClientId}' uses a client secret outside Development. Use private_key_jwt.";
        }

        if (Kind == ClientKind.Browser)
        {
            if (string.IsNullOrWhiteSpace(Audience))
            {
                yield return $"Browser client '{ClientId}' has no Audience.";
            }

            if (RedirectUris.Count == 0 || RedirectUris.Any(uri => !Uri.TryCreate(uri, UriKind.Absolute, out _)))
            {
                yield return $"Browser client '{ClientId}' needs absolute redirect URIs.";
            }

            if (AbsoluteSessionLifetime <= TimeSpan.Zero || RefreshTokenLifetime <= TimeSpan.Zero || EffectiveAccessTokenLifetime <= TimeSpan.Zero)
            {
                yield return $"Browser client '{ClientId}' has a non-positive lifetime.";
            }
        }

        if (Kind == ClientKind.Service && EffectiveAccessTokenLifetime <= TimeSpan.Zero)
        {
            yield return $"Service client '{ClientId}' has a non-positive access token lifetime.";
        }

        if (Kind == ClientKind.Service && ServiceScopes.Any(scope => string.IsNullOrWhiteSpace(scope.Scope) || string.IsNullOrWhiteSpace(scope.Audience)))
        {
            yield return $"Service client '{ClientId}' has an incomplete scope registration.";
        }
    }
}

/// <summary>Identity protocol server settings (<c>Identity</c> configuration section).</summary>
internal sealed class IdentityServerSettings
{
    public const string SectionName = "Identity";

    /// <summary>The single, stable public issuer URI of this environment (ADR-017 section 10).</summary>
    public Uri? Issuer { get; set; }

    /// <summary>The admin console BFF client. Refresh-token reuse there ends every session of the user.</summary>
    public string AdminConsoleClientId { get; set; } = "bff-admin";

    public List<ClientRegistration> Clients { get; set; } = [];

    public Keys.KeyRingSettings Keys { get; set; } = new();

    public RateLimitSettings RateLimits { get; set; } = new();

    public AccountAdministrationSettings AccountAdministration { get; set; } = new();

    public AccountVerificationSettings AccountVerification { get; set; } = new();
}

/// <summary>
/// The internal account email verification API (IDENTITY_API_CONTRACT.md section 2.13, ADR-021). Only service clients
/// that hold the verification scope <b>and</b> are listed here may call it. An empty list denies every caller. Its
/// audience is Identity's service audience, <see cref="AccountAdministrationSettings.Audience"/>.
/// </summary>
internal sealed class AccountVerificationSettings
{
    /// <summary>The scope a service client must hold.</summary>
    public const string Scope = "svc.identity.account-verification";

    /// <summary>The service clients (<c>svc-</c> prefix) allowed to verify account emails, for example <c>svc-tenant-merchant</c>.</summary>
    public List<string> AllowedClientIds { get; set; } = [];

    public IEnumerable<string> Validate()
    {
        foreach (var clientId in AllowedClientIds.Where(id =>
            !id.StartsWith(ClientRegistration.ServiceClientPrefix, StringComparison.Ordinal)
            || id.Length == ClientRegistration.ServiceClientPrefix.Length))
        {
            yield return $"Identity:AccountVerification:AllowedClientIds may only list service clients ('{clientId}').";
        }
    }
}

/// <summary>
/// The internal account lifecycle API (IDENTITY_API_CONTRACT.md section 2.12). Only service clients that hold the
/// account-lifecycle scope <b>and</b> are listed here may call it. An empty list denies every caller.
/// </summary>
internal sealed class AccountAdministrationSettings
{
    /// <summary>The scope a service client must hold. Its audience is <see cref="Audience"/>.</summary>
    public const string Scope = "svc.identity.account-lifecycle";

    /// <summary>Identity's own audience for service tokens (ADR-017 section 3: the audience of the called service).</summary>
    public string Audience { get; set; } = "identity-api";

    /// <summary>The service clients (<c>svc-</c> prefix) allowed to change account status, for example <c>svc-admin</c>.</summary>
    public List<string> AllowedClientIds { get; set; } = [];

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(Audience))
        {
            yield return "Identity:AccountAdministration:Audience is required.";
        }

        foreach (var clientId in AllowedClientIds.Where(id => !id.StartsWith(ClientRegistration.ServiceClientPrefix, StringComparison.Ordinal)))
        {
            yield return $"Identity:AccountAdministration:AllowedClientIds may only list service clients ('{clientId}').";
        }
    }
}

/// <summary>Per-IP limits on the credential pages (ADR-017 section 9).</summary>
internal sealed class RateLimitSettings
{
    public int CredentialRequestsPerMinute { get; set; } = 20;
}
