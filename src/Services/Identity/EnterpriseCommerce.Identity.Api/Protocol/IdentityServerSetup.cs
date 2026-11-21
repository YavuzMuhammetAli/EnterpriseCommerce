using EnterpriseCommerce.Identity.Application.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EnterpriseCommerce.Identity.Api.Protocol.Keys;
using EnterpriseCommerce.Identity.Application.Security;
using EnterpriseCommerce.Identity.Domain.Security;
using EnterpriseCommerce.Identity.Infrastructure.Retention;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>Composition of the OAuth 2.0 / OpenID Connect server, the Identity SSO cookie and the credential-page limits.</summary>
internal static class IdentityServerSetup
{
    public const string CredentialRateLimitPolicy = "credential-pages";

    /// <summary>The Identity SSO cookie (ADR-017 section 9): host-only, Secure, HttpOnly, SameSite=Lax.</summary>
    public const string SsoCookieName = "__Host-ec-identity";

    public const string AntiforgeryCookieName = "__Host-ec-identity-af";

    /// <summary>The OpenID Connect end-session endpoint, served by an Identity page (POST-only sign-out).</summary>
    public const string EndSessionPath = "connect/endsession";

    /// <summary>Bearer access tokens of users, obtained by a browser client (BFF). Never the SSO cookie.</summary>
    public const string UserBearerPolicy = "user-bearer";

    /// <summary>Service tokens of the clients allowed to change account status (internal API only).</summary>
    public const string AccountAdministrationPolicy = "account-administration";

    /// <summary>Service tokens of the clients allowed to verify account emails (internal API only, ADR-021).</summary>
    public const string AccountVerificationPolicy = "account-verification";

    private static readonly TimeSpan SsoAbsoluteLifetime = TimeSpan.FromHours(12);

    /// <summary>The clock skew every validator allows (ADR-017 section 4).</summary>
    public static readonly TimeSpan ValidationClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>Authorization code lifetime (PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.4).</summary>
    public static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>ID token lifetime (PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.4).</summary>
    public static readonly TimeSpan IdentityTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The server-wide default access token lifetime; clients override it per token.</summary>
    public static readonly TimeSpan DefaultAccessTokenLifetime = TimeSpan.FromMinutes(10);

    public static WebApplicationBuilder AddIdentityProtocolServer(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var settings = builder.Configuration.GetSection(IdentityServerSettings.SectionName).Get<IdentityServerSettings>() ?? new();
        var isDevelopment = builder.Environment.IsDevelopment();

        var errors = new List<string>();
        if (settings.Issuer is not { IsAbsoluteUri: true } issuer || issuer.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Identity:Issuer must be an absolute https URI.");
        }

        errors.AddRange(settings.Clients.SelectMany(client => client.Validate(isDevelopment)));
        errors.AddRange(settings.Clients.GroupBy(c => c.ClientId, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"Duplicate client '{g.Key}'."));
        errors.AddRange(settings.AccountAdministration.Validate());
        errors.AddRange(settings.AccountVerification.Validate());
        foreach (var legacy in new[] { "SigningCertificatePath", "SigningCertificatePassword", "EncryptionCertificatePath", "EncryptionCertificatePassword" })
        {
            if (builder.Configuration.GetSection($"{IdentityServerSettings.SectionName}:Keys:{legacy}").Exists())
            {
                errors.Add($"Identity:Keys:{legacy} is replaced by the key ring (Identity:Keys:Signing and Identity:Keys:Encryption).");
            }
        }
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid Identity configuration: " + string.Join(" ", errors));
        }

        services.Configure<IdentityServerSettings>(builder.Configuration.GetSection(IdentityServerSettings.SectionName));
        services.AddHostedService<ClientRegistrationSynchronizer>();

        // Retention keeps replay-protection records until every token they could reject has expired (S6.2).
        services.UseReplayProtectionLifetimes(TokenLifetimes(settings));

        // The key ring (S6.3). A retired key stays loaded until nothing it signed or encrypted can still be accepted.
        var retentionSettings = builder.Configuration.GetSection(IdentityRetentionOptions.SectionName).Get<IdentityRetentionOptions>() ?? new();
        var replayWindow = ReplayProtectionWindow.Compute(TokenLifetimes(settings), retentionSettings.SafetyMargin, retentionSettings.MinimumReplayProtectionWindow);
        var keyRing = KeyRingLoader.Load(
            settings.Keys,
            new CertificateFileKeySource(),
            new KeyRetention(replayWindow + ValidationClockSkew + settings.Keys.ValidatorCacheAllowance, replayWindow + ValidationClockSkew),
            DateTimeOffset.UtcNow,
            isDevelopment);
        services.AddSingleton(keyRing);
        services.AddHostedService<KeyRingReporter>();

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, options =>
            {
                options.Cookie.Name = SsoCookieName;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.Cookie.Domain = null;
                options.Cookie.IsEssential = true;
                options.LoginPath = "/account/login";
                options.AccessDeniedPath = "/account/login";
                options.ReturnUrlParameter = "returnUrl";
                options.ExpireTimeSpan = TimeSpan.FromHours(1);
                options.SlidingExpiration = true;
                options.Events.OnValidatePrincipal = ValidateSsoSessionAsync;
            });

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = AntiforgeryCookieName;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(CredentialRateLimitPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = settings.RateLimits.CredentialRequestsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });

        services.AddOpenIddict()
            .AddServer(options =>
            {
                options.SetIssuer(settings.Issuer!);
                options.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke")
                    .SetEndSessionEndpointUris(EndSessionPath)
                    .SetJsonWebKeySetEndpointUris(".well-known/jwks");

                options.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .AllowClientCredentialsFlow();

                options.RequireProofKeyForCodeExchange();

                // ADR-017 section 5: no reuse leeway. A redeemed refresh token is never accepted again.
                options.SetRefreshTokenReuseLeeway(null);
                options.SetAuthorizationCodeLifetime(AuthorizationCodeLifetime);
                options.SetIdentityTokenLifetime(IdentityTokenLifetime);
                options.SetAccessTokenLifetime(DefaultAccessTokenLifetime);

                options.RegisterScopes(
                    [Scopes.OpenId, Scopes.OfflineAccess, .. settings.Clients.SelectMany(c => c.ServiceScopes).Select(s => s.Scope).Distinct(StringComparer.Ordinal)]);

                // Signed, unencrypted JWT access tokens (ADR-015 R3, ADR-017 section 2).
                options.DisableAccessTokenEncryption();
                foreach (var key in keyRing.Of(KeyUse.Signing))
                {
                    options.AddSigningKey(key.SecurityKey);
                }

                foreach (var key in keyRing.Of(KeyUse.Encryption))
                {
                    options.AddEncryptionKey(key.SecurityKey);
                }

                options.Configure(server =>
                {
                    // ADR-016 section 2: PKCE with S256 only. OpenIddict also accepts "plain" by default.
                    server.CodeChallengeMethods.Clear();
                    server.CodeChallengeMethods.Add(CodeChallengeMethods.Sha256);

                    // ADR-016 section 2: private_key_jwt for confidential clients in deployed environments.
                    server.ClientAuthenticationMethods.Clear();
                    server.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.PrivateKeyJwt);
                    if (isDevelopment)
                    {
                        server.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.ClientSecretBasic);
                        server.ClientAuthenticationMethods.Add(ClientAuthenticationMethods.ClientSecretPost);
                    }
                });

                options.AddEventHandler(RefreshTokenReuseHook.Descriptor);
                options.AddEventHandler(RevocationSessionHook.Descriptor);

                options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();
            })
            .AddValidation(options =>
            {
                // Identity's own REST endpoints accept the front-end audiences of its browser clients (logout-all) and
                // its own service audience (internal account lifecycle), validated locally like every other service
                // does (ADR-017 sections 3, 4 and 6). Each endpoint's policy then requires exactly one kind.
                options.UseLocalServer();
                options.AddAudiences([.. BrowserAudiences(settings), settings.AccountAdministration.Audience]);
                options.UseAspNetCore();
            });

        // Registered after OpenIddict's own post-configuration, which sorts the credentials by date (V24).
        services.AddSingleton<IPostConfigureOptions<OpenIddictServerOptions>>(new ActiveKeysFirst(keyRing));

        var audiences = BrowserAudiences(settings).ToHashSet(StringComparer.Ordinal);
        var administration = settings.AccountAdministration;
        var administrationClients = administration.AllowedClientIds.ToHashSet(StringComparer.Ordinal);
        var verificationClients = settings.AccountVerification.AllowedClientIds.ToHashSet(StringComparer.Ordinal);
        services.AddAuthorizationBuilder()
            .AddPolicy(UserBearerPolicy, policy => policy
                .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(Claims.Subject)
                .RequireClaim(IdentityClaims.PrincipalType, IdentityClaims.UserPrincipal)
                // Defense in depth: never accept a token without one of the front-end audiences, even if the
                // validation handler had no audience configured.
                .RequireAssertion(context => context.User.GetAudiences().Any(audiences.Contains)))
            .AddPolicy(AccountAdministrationPolicy, policy => policy
                .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(IdentityClaims.PrincipalType, IdentityClaims.ServicePrincipal)
                // A service token for Identity, with the account-lifecycle scope, from an explicitly allowed service
                // client. The calling client is authorized, never an administrator role (section 5.6.6): Admin
                // authorizes its administrators before it calls.
                .RequireAssertion(context =>
                    context.User.GetAudiences().Contains(administration.Audience, StringComparer.Ordinal)
                    && context.User.HasScope(AccountAdministrationSettings.Scope)
                    && CallingClientOf(context.User) is { } client
                    && client.StartsWith(ClientRegistration.ServiceClientPrefix, StringComparison.Ordinal)
                    && administrationClients.Contains(client)))
            .AddPolicy(AccountVerificationPolicy, policy => policy
                .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .RequireClaim(IdentityClaims.PrincipalType, IdentityClaims.ServicePrincipal)
                // A service token for Identity, with the verification scope, from an explicitly allowed service client
                // (ADR-021). The lifecycle scope does not grant it, and this scope grants nothing else.
                .RequireAssertion(context =>
                    context.User.GetAudiences().Contains(administration.Audience, StringComparer.Ordinal)
                    && context.User.HasScope(AccountVerificationSettings.Scope)
                    && CallingClientOf(context.User) is { } client
                    && client.StartsWith(ClientRegistration.ServiceClientPrefix, StringComparison.Ordinal)
                    && verificationClients.Contains(client)));

        return builder;
    }

    /// <summary>The service client that obtained a validated service token (its <c>client_id</c>).</summary>
    public static string? CallingClientOf(ClaimsPrincipal principal) =>
        principal.GetClaim(Claims.ClientId) is { Length: > 0 } clientId
        && principal.GetClaim(Claims.Subject) is { } subject
        && string.Equals(subject, clientId, StringComparison.Ordinal)
            ? clientId
            : null;

    /// <summary>
    /// Validates the Identity SSO cookie on every request (ADR-017 section 7): the security version and account
    /// state must still allow the session, and the absolute lifetime must not have passed. A database failure
    /// throws (503) instead of clearing the cookie.
    /// </summary>
    private static async Task ValidateSsoSessionAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var userId = principal?.GetClaim(Claims.Subject);
        var issued = context.Properties.IssuedUtc;

        if (principal is null || userId is null || issued is null || DateTimeOffset.UtcNow - issued > SsoAbsoluteLifetime)
        {
            IdentityMetrics.SsoSessionRejections.Add(1, IdentityMetrics.Tag("reason", "absolute_lifetime"));
            await RejectAsync(context);
            return;
        }

        var reader = context.HttpContext.RequestServices.GetRequiredService<SecurityStateReader>();
        var state = await reader.ReadAsync(userId, context.HttpContext.RequestAborted);

        if (!state.AllowsSession(principal.GetSecurityVersion()))
        {
            IdentityMetrics.SsoSessionRejections.Add(1, IdentityMetrics.Tag("reason", state.Account is { CanSignIn: true } ? "stale_session" : "account_not_allowed"));
            await RejectAsync(context);
        }

        static async Task RejectAsync(CookieValidatePrincipalContext context)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    }

    /// <summary>Every lifetime that can bound a token Identity issues: per client, plus the server-wide ones.</summary>
    public static IReadOnlyCollection<TimeSpan> TokenLifetimes(IdentityServerSettings settings) =>
    [
        AuthorizationCodeLifetime,
        IdentityTokenLifetime,
        DefaultAccessTokenLifetime,
        .. settings.Clients.Select(client => client.EffectiveAccessTokenLifetime),
        .. settings.Clients.Where(client => client.Kind == ClientKind.Browser)
            .SelectMany(client => new[] { client.RefreshTokenLifetime, client.AbsoluteSessionLifetime }),
    ];

    private static IEnumerable<string> BrowserAudiences(IdentityServerSettings settings) =>
        settings.Clients
            .Where(client => client.Kind == ClientKind.Browser && !string.IsNullOrWhiteSpace(client.Audience))
            .Select(client => client.Audience!)
            .Distinct(StringComparer.Ordinal);

    /// <summary>Creates the Identity SSO cookie principal. The security version pins the browser session (ADR-017 section 7).</summary>
    public static ClaimsPrincipal CreateSsoPrincipal(string userId, long securityVersion)
    {
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, userId)
            .SetClaim(IdentityClaims.SecurityVersion, securityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .SetClaims(Claims.AuthenticationMethodReference, ["pwd"]);
        return new ClaimsPrincipal(identity);
    }
}
