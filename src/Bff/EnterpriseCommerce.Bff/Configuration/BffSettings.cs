namespace EnterpriseCommerce.Bff.Configuration;

/// <summary>
/// The single front-end deployment this BFF instance serves (section <c>Bff</c>, ADR-016 section 2). The section holds
/// exactly one client configuration: a BFF instance never serves two front ends.
/// </summary>
internal sealed class BffSettings
{
    public const string SectionName = "Bff";

    /// <summary>The Redis connection string of the dedicated session store (ADR-016 section 3).</summary>
    public const string SessionStoreConnectionName = "BffSessions";

    /// <summary>A short deployment name (for example <c>storefront</c>): cookie names, key prefix and logs use it.</summary>
    public string Deployment { get; set; } = string.Empty;

    /// <summary>The OAuth client ID registered at Identity for this deployment (for example <c>bff-storefront</c>).</summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>The access-token audience of this front end (for example <c>ec-storefront</c>, ADR-017 section 4).</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>The Identity issuer (absolute HTTPS URI with a trailing slash).</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>The public origin of this front end (scheme, host and port), for example <c>https://shop.example</c>.</summary>
    public string PublicOrigin { get; set; } = string.Empty;

    /// <summary>The local path the browser lands on after Identity's end-session flow.</summary>
    public string PostLogoutLandingPath { get; set; } = "/";

    public BffClientKeySettings ClientKey { get; set; } = new();

    public BffCookieSettings Cookies { get; set; } = new();

    public BffSessionSettings Session { get; set; } = new();

    public BffRefreshSettings Refresh { get; set; } = new();

    public BffMetadataSettings Metadata { get; set; } = new();

    /// <summary>The Gateway base address that <c>/api/*</c> is forwarded to with the session's access token. Optional.</summary>
    public string? DownstreamApiAddress { get; set; }

    /// <summary>The Data Protection key directory. Required outside Development; the production key store is deferred (D19).</summary>
    public string? DataProtectionKeyDirectory { get; set; }

    /// <summary>
    /// A PKCS#12 file whose certificate encrypts the Data Protection keys at rest (a mounted secret). Required outside
    /// Development. The production key store itself is deferred (D19).
    /// </summary>
    public string? DataProtectionCertificatePath { get; set; }

    /// <summary>The password of <see cref="DataProtectionCertificatePath"/>, from a protected store or the environment.</summary>
    public string? DataProtectionCertificatePassword { get; set; }

    /// <summary>The Redis key namespace of this deployment. Defaults to <c>bff:{Deployment}</c>.</summary>
    public string? RedisKeyPrefix { get; set; }

    public string KeyPrefix => string.IsNullOrWhiteSpace(RedisKeyPrefix) ? $"bff:{Deployment}" : RedisKeyPrefix;

    /// <summary>The OAuth redirect URI registered at Identity for this deployment.</summary>
    public Uri RedirectUri => new(new Uri(PublicOrigin), BffPaths.Callback);

    /// <summary>The post-logout redirect URI registered at Identity for this deployment.</summary>
    public Uri PostLogoutRedirectUri => new(new Uri(PublicOrigin), BffPaths.SignedOut);

    public Uri AuthorityUri => new(Authority);
}

/// <summary>The private key for <c>private_key_jwt</c> client authentication (ADR-016 section 2).</summary>
internal sealed class BffClientKeySettings
{
    /// <summary>A PEM file holding the RSA or EC private key (deployed environments: a mounted secret).</summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>The PEM itself, for local development from user secrets or the environment. Never from the repository.</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>The <c>kid</c> of the public key registered in the client's JWKS at Identity.</summary>
    public string KeyId { get; set; } = string.Empty;
}

internal sealed class BffCookieSettings
{
    /// <summary>
    /// Whether the session cookie uses the <c>__Host-Http-</c> prefix (ADR-016 section 5). Browsers that do not know the
    /// prefix still apply the <c>__Host-</c> rules to it. Set to false only for a browser that is shown to mishandle it.
    /// </summary>
    public bool UseHttpOnlyPrefix { get; set; } = true;

    /// <summary>The cookie name stem; defaults to <c>ec-{Deployment}</c>. Distinct per front end.</summary>
    public string? NameStem { get; set; }
}

internal sealed class BffSessionSettings
{
    /// <summary>The absolute session lifetime; never extended (ADR-017 section 6, lifetimes table).</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The idle timeout: a session unused for this long ends (sliding, capped by the absolute lifetime).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Whether "keep me signed in" (a persistent cookie, bounded by the absolute lifetime) is offered.</summary>
    public bool AllowPersistent { get; set; }
}

internal sealed class BffRefreshSettings
{
    /// <summary>Refresh starts this long before the access token expires (or a third of its lifetime, if smaller).</summary>
    public TimeSpan Threshold { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The timeout of one refresh request to Identity.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The per-session refresh lease; longer than the request timeout.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a request waits for another instance's refresh before using what it has.</summary>
    public TimeSpan WaitForOtherRefresh { get; set; } = TimeSpan.FromSeconds(6);
}

/// <summary>
/// How the BFF caches Identity's discovery document and JWKS (Phase 2.3 S6.3). The refresh interval bounds how long a
/// retired signing key can still be used for validation here, so it must not exceed Identity's
/// <c>Identity:Keys:ValidatorCacheAllowance</c>. On an unknown key ID the handler requests a refresh at once, at most
/// once per <see cref="MinimumRefreshInterval"/>.
/// </summary>
internal sealed class BffMetadataSettings
{
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan MinimumRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>The BFF's own endpoints (P22 section 4.1).</summary>
internal static class BffPaths
{
    public const string Prefix = "/bff";
    public const string Login = "/bff/login";
    public const string Callback = "/bff/callback";
    public const string User = "/bff/user";
    public const string Logout = "/bff/logout";
    public const string LogoutAll = "/bff/logout-all";
    public const string SignedOut = "/bff/signed-out";
    public const string Api = "/api";
}

internal static class BffSettingsValidation
{
    public static IEnumerable<string> Validate(BffSettings settings, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(settings.Deployment) || !settings.Deployment.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            yield return "Bff:Deployment must be a lower-case name (letters, digits, '-').";
        }

        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            yield return "Bff:ClientId is required.";
        }
        else if (settings.ClientId.StartsWith("svc-", StringComparison.Ordinal))
        {
            yield return "Bff:ClientId must not use the 'svc-' prefix reserved for service clients (ADR-017 section 4).";
        }

        if (string.IsNullOrWhiteSpace(settings.Audience))
        {
            yield return "Bff:Audience is required (one audience per front end, ADR-017 section 4).";
        }

        if (!Uri.TryCreate(settings.Authority, UriKind.Absolute, out var authority) || authority.Scheme != Uri.UriSchemeHttps || !settings.Authority.EndsWith('/'))
        {
            yield return "Bff:Authority must be the absolute HTTPS Identity issuer, ending with '/'.";
        }

        if (!Uri.TryCreate(settings.PublicOrigin, UriKind.Absolute, out var origin) || origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query) || settings.PublicOrigin.EndsWith('/'))
        {
            yield return "Bff:PublicOrigin must be an origin (scheme, host, optional port) without a path or trailing '/'.";
        }
        else if (origin.Scheme != Uri.UriSchemeHttps)
        {
            // __Host- cookies require Secure, so even local development uses HTTPS (ADR-016 section 7).
            yield return "Bff:PublicOrigin must use HTTPS.";
        }

        if (!settings.PostLogoutLandingPath.StartsWith('/') || settings.PostLogoutLandingPath.StartsWith("//", StringComparison.Ordinal))
        {
            yield return "Bff:PostLogoutLandingPath must be a local path.";
        }

        var hasPath = !string.IsNullOrWhiteSpace(settings.ClientKey.PrivateKeyPath);
        var hasPem = !string.IsNullOrWhiteSpace(settings.ClientKey.PrivateKeyPem);
        if (hasPath == hasPem)
        {
            yield return "Bff:ClientKey needs exactly one of PrivateKeyPath or PrivateKeyPem (private_key_jwt).";
        }

        if (string.IsNullOrWhiteSpace(settings.ClientKey.KeyId))
        {
            yield return "Bff:ClientKey:KeyId is required.";
        }

        var session = settings.Session;
        if (session.AbsoluteLifetime <= TimeSpan.Zero || session.IdleTimeout <= TimeSpan.Zero || session.IdleTimeout > session.AbsoluteLifetime)
        {
            yield return "Bff:Session needs a positive AbsoluteLifetime and an IdleTimeout no longer than it.";
        }

        var refresh = settings.Refresh;
        if (refresh.Threshold < TimeSpan.Zero || refresh.RequestTimeout <= TimeSpan.Zero || refresh.LeaseDuration <= refresh.RequestTimeout || refresh.WaitForOtherRefresh < TimeSpan.Zero)
        {
            yield return "Bff:Refresh needs a non-negative Threshold and a LeaseDuration longer than the RequestTimeout.";
        }

        var metadata = settings.Metadata;
        if (metadata.RefreshInterval < TimeSpan.FromMinutes(5) || metadata.RefreshInterval > TimeSpan.FromHours(24)
            || metadata.MinimumRefreshInterval < TimeSpan.FromSeconds(1) || metadata.MinimumRefreshInterval > metadata.RefreshInterval)
        {
            yield return "Bff:Metadata needs a RefreshInterval of 5 minutes to 24 hours and a MinimumRefreshInterval of 1 second up to it.";
        }

        if (settings.DownstreamApiAddress is { Length: > 0 } downstream && !Uri.TryCreate(downstream, UriKind.Absolute, out _))
        {
            yield return "Bff:DownstreamApiAddress must be an absolute URI.";
        }

        if (!isDevelopment)
        {
            if (string.IsNullOrWhiteSpace(settings.DataProtectionKeyDirectory))
            {
                yield return "Bff:DataProtectionKeyDirectory is required outside Development (shared by the instances of one deployment).";
            }

            if (string.IsNullOrWhiteSpace(settings.DataProtectionCertificatePath) || string.IsNullOrEmpty(settings.DataProtectionCertificatePassword))
            {
                yield return "Bff:DataProtectionCertificatePath and its password are required outside Development (keys are encrypted at rest).";
            }

            if (hasPem)
            {
                yield return "Bff:ClientKey:PrivateKeyPem is for local development only; use PrivateKeyPath (a mounted secret).";
            }
        }
    }
}
