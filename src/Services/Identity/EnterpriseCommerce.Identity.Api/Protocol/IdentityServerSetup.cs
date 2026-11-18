using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using EnterpriseCommerce.Identity.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace EnterpriseCommerce.Identity.Api.Protocol;

/// <summary>Composition of the OAuth 2.0 / OpenID Connect server, the Identity SSO cookie and the credential-page limits.</summary>
internal static class IdentityServerSetup
{
    public const string CredentialRateLimitPolicy = "credential-pages";

    /// <summary>The Identity SSO cookie (ADR-017 section 9): host-only, Secure, HttpOnly, SameSite=Lax.</summary>
    public const string SsoCookieName = "__Host-ec-identity";

    public const string AntiforgeryCookieName = "__Host-ec-identity-af";

    private static readonly TimeSpan SsoAbsoluteLifetime = TimeSpan.FromHours(12);

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
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid Identity configuration: " + string.Join(" ", errors));
        }

        services.Configure<IdentityServerSettings>(builder.Configuration.GetSection(IdentityServerSettings.SectionName));
        services.AddHostedService<ClientRegistrationSynchronizer>();

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
                    .SetJsonWebKeySetEndpointUris(".well-known/jwks");

                options.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow()
                    .AllowClientCredentialsFlow();

                options.RequireProofKeyForCodeExchange();

                // ADR-017 section 5: no reuse leeway. A redeemed refresh token is never accepted again.
                options.SetRefreshTokenReuseLeeway(null);
                options.SetAuthorizationCodeLifetime(TimeSpan.FromSeconds(60));
                options.SetIdentityTokenLifetime(TimeSpan.FromMinutes(5));
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));

                options.RegisterScopes(
                    [Scopes.OpenId, Scopes.OfflineAccess, .. settings.Clients.SelectMany(c => c.ServiceScopes).Select(s => s.Scope).Distinct(StringComparer.Ordinal)]);

                // Signed, unencrypted JWT access tokens (ADR-015 R3, ADR-017 section 2).
                options.DisableAccessTokenEncryption();
                AddKeys(options, settings.Keys, isDevelopment);

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

                options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
            });

        return builder;
    }

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
            await RejectAsync(context);
            return;
        }

        var reader = context.HttpContext.RequestServices.GetRequiredService<SecurityStateReader>();
        var state = await reader.ReadAsync(userId, context.HttpContext.RequestAborted);

        if (!state.AllowsSession(principal.GetSecurityVersion()))
        {
            await RejectAsync(context);
        }

        static async Task RejectAsync(CookieValidatePrincipalContext context)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    }

    /// <summary>Creates the Identity SSO cookie principal. The security version pins the browser session (ADR-017 section 7).</summary>
    public static ClaimsPrincipal CreateSsoPrincipal(string userId, long securityVersion)
    {
        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, userId)
            .SetClaim(IdentityClaims.SecurityVersion, securityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .SetClaims(Claims.AuthenticationMethodReference, ["pwd"]);
        return new ClaimsPrincipal(identity);
    }

    private static void AddKeys(OpenIddictServerBuilder options, SigningSettings keys, bool isDevelopment)
    {
        if (!string.IsNullOrWhiteSpace(keys.SigningCertificatePath))
        {
            var signing = X509CertificateLoader.LoadPkcs12FromFile(keys.SigningCertificatePath, keys.SigningCertificatePassword);
            var encryption = X509CertificateLoader.LoadPkcs12FromFile(
                keys.EncryptionCertificatePath ?? throw new InvalidOperationException("Identity:Keys:EncryptionCertificatePath is required."),
                keys.EncryptionCertificatePassword);

            if (signing.GetRSAPublicKey() is not { KeySize: >= 3072 })
            {
                throw new InvalidOperationException("The signing certificate must use an RSA key of at least 3072 bits (ADR-017 section 2).");
            }

            options.AddSigningCertificate(signing).AddEncryptionCertificate(encryption);
            return;
        }

        if (!isDevelopment)
        {
            throw new InvalidOperationException("Signing and encryption certificates must be configured outside Development (ADR-017 section 12).");
        }

        // Development and tests only: keys generated in memory at startup, never stored or committed.
        // Tokens become invalid when the process restarts.
        options.AddSigningKey(new RsaSecurityKey(RSA.Create(3072)) { KeyId = Guid.NewGuid().ToString("N") });
        options.AddEncryptionKey(new RsaSecurityKey(RSA.Create(3072)) { KeyId = Guid.NewGuid().ToString("N") });
    }
}
