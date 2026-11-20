using System.Globalization;
using EnterpriseCommerce.Bff.Configuration;
using EnterpriseCommerce.Bff.Logging;
using EnterpriseCommerce.Bff.Security;
using EnterpriseCommerce.Bff.Sessions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseCommerce.Bff.Protocol;

/// <summary>
/// The BFF as a confidential OAuth client of Identity (ADR-016 section 2): authorization code, PKCE <c>S256</c>,
/// <c>response_mode=query</c>, <c>private_key_jwt</c> at the token endpoint, single-use <c>state</c> and <c>nonce</c>.
/// The authenticated session is created only after the handler has validated the whole response.
/// </summary>
internal static class OidcSetup
{
    public const string Scheme = "identity";

    /// <summary>Authentication property: the challenge is a silent re-authorization (<c>prompt=none</c>).</summary>
    public const string SilentItem = "bff.silent";

    /// <summary>Authentication property: the validated local return URL.</summary>
    public const string ReturnUrlItem = "bff.return";

    /// <summary>Protocol errors that mean "no usable Identity session": fall back to an interactive login.</summary>
    internal static readonly HashSet<string> InteractionErrors = new(StringComparer.Ordinal)
    {
        "login_required", "interaction_required", "consent_required", "account_selection_required", "access_denied",
    };

    public static AuthenticationBuilder AddIdentityClient(this AuthenticationBuilder builder) =>
        builder.AddOpenIdConnect(Scheme, _ => { });

    /// <summary>Configures the handler from the BFF settings and the DI container.</summary>
    internal sealed class ConfigureOptions(
        IOptions<BffSettings> settings,
        BffCookieNames cookieNames,
        IHttpClientFactory httpClients) : IConfigureNamedOptions<OpenIdConnectOptions>
    {
        public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

        public void Configure(string? name, OpenIdConnectOptions options)
        {
            if (name != Scheme)
            {
                return;
            }

            var bff = settings.Value;
            options.Authority = bff.Authority;
            options.ClientId = bff.ClientId;
            options.ClientSecret = null;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("offline_access");
            options.CallbackPath = BffPaths.Callback;
            options.SignInScheme = BffSessionDefaults.Scheme;
            options.SignOutScheme = BffSessionDefaults.Scheme;
            options.SaveTokens = true;
            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.DisableTelemetry = true;
            options.RequireHttpsMetadata = true;
            options.Backchannel = httpClients.CreateClient(IdentityProtocolClient.HttpClientName);
            options.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(5);

            // Bounded metadata and JWKS caching (S6.3): a signing key Identity retired stops being trusted here within
            // RefreshInterval. An ID token with an unknown key ID makes the handler request a refresh immediately.
            options.AutomaticRefreshInterval = bff.Metadata.RefreshInterval;
            options.RefreshInterval = bff.Metadata.MinimumRefreshInterval;

            // Pushed authorization requests are a later proposal (P22 section 5.2); Identity does not offer them.
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;

            // ID token validation: issuer, audience (the client ID), signature from Identity's JWKS, lifetime, nonce.
            options.TokenValidationParameters.ValidIssuer = bff.AuthorityUri.AbsoluteUri;
            options.TokenValidationParameters.ValidAudience = bff.ClientId;
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidateLifetime = true;
            options.TokenValidationParameters.RequireSignedTokens = true;
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
            options.TokenValidationParameters.NameClaimType = BffClaims.Subject;
            options.ProtocolValidator.RequireNonce = true;

            // Correlation and nonce cookies: __Host-, Secure, HttpOnly, SameSite=Lax (the callback is a cross-site
            // top-level GET), Path=/ for the prefix, deleted by the handler at the callback (ADR-016 section 5).
            foreach (var cookie in new[] { options.CorrelationCookie, options.NonceCookie })
            {
                cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.HttpOnly = true;
                cookie.SameSite = SameSiteMode.Lax;
                cookie.Path = "/";
                cookie.Domain = null;
                cookie.IsEssential = true;
            }

            options.CorrelationCookie.Name = cookieNames.Correlation;
            options.NonceCookie.Name = cookieNames.Nonce;

            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = RedirectToIdentityProvider,
                OnAuthorizationCodeReceived = AuthorizationCodeReceived,
                OnTokenValidated = TokenValidated,
                OnTicketReceived = TicketReceived,
                OnRemoteFailure = RemoteFailure,
            };
        }

        private Task RedirectToIdentityProvider(RedirectContext context)
        {
            // The registered redirect URI comes from configuration, never from the request's Host header.
            // The handler stores this value for the code exchange after the event.
            context.ProtocolMessage.RedirectUri = settings.Value.RedirectUri.AbsoluteUri;

            // response_mode=query explicitly (ADR-016 section 2); the handler omits the default otherwise.
            context.ProtocolMessage.ResponseMode = OpenIdConnectResponseMode.Query;
            if (context.Properties.Items.TryGetValue(SilentItem, out var silent) && silent == "1")
            {
                context.ProtocolMessage.Prompt = OpenIdConnectPrompt.None;
            }

            return Task.CompletedTask;
        }

        private Task AuthorizationCodeReceived(AuthorizationCodeReceivedContext context)
        {
            // private_key_jwt: a fresh assertion for this one exchange. No client secret is ever sent.
            var request = context.TokenEndpointRequest!;
            request.ClientSecret = null;
            request.RedirectUri = settings.Value.RedirectUri.AbsoluteUri;
            request.ClientAssertionType = ClientAssertionFactory.AssertionType;
            request.ClientAssertion = context.HttpContext.RequestServices.GetRequiredService<ClientAssertionFactory>().Create();
            return Task.CompletedTask;
        }

        private async Task TokenValidated(TokenValidatedContext context)
        {
            var response = context.TokenEndpointResponse;
            var identity = context.HttpContext.RequestServices.GetRequiredService<IdentityProtocolClient>();
            if (response is null || string.IsNullOrEmpty(response.AccessToken) || string.IsNullOrEmpty(response.RefreshToken) || string.IsNullOrEmpty(response.IdToken))
            {
                context.Fail("The token response is incomplete.");
                return;
            }

            // The handler skips the signature of an ID token from the token endpoint (OIDC Core 3.1.3.7 allows TLS to
            // stand in for it), which would let an unsigned (alg=none) token through. Identity signs every ID token,
            // so the BFF requires a valid signature from the issuer's JWKS as well.
            if (!await IsSignedByIssuerAsync(context, response.IdToken))
            {
                context.Fail("The ID token is not signed by the issuer.");
                return;
            }

            if (!identity.HasExpectedAudience(response.AccessToken))
            {
                context.Fail("The access token is not issued for this front end.");
                return;
            }

            if (string.IsNullOrEmpty(context.Principal?.FindFirst(BffClaims.Subject)?.Value))
            {
                context.Fail("The ID token has no subject.");
                return;
            }

            var now = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            // Read back with GetTokenValue by the session handler, which moves every token into the session record.
            context.Properties!.Items[".Token." + BffTokenNames.IssuedAt] = now.ToString("o", CultureInfo.InvariantCulture);
        }

        private static async Task<bool> IsSignedByIssuerAsync(TokenValidatedContext context, string idToken)
        {
            var configuration = await context.Options.ConfigurationManager!.GetConfigurationAsync(context.HttpContext.RequestAborted);
            var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                // Issuer, audience, lifetime and nonce were validated by the handler; this checks the signature only.
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
            });
            return result.IsValid;
        }

        private Task TicketReceived(TicketReceivedContext context)
        {
            var properties = context.Properties!;
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcSetup));
            BffLog.LoginSucceeded(logger, settings.Value.Deployment, properties.Items.ContainsKey(SilentItem));
            return Task.CompletedTask;
        }

        private async Task RemoteFailure(RemoteFailureContext context)
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcSetup));
            var error = ProtocolError(context);
            var silent = context.Properties?.Items.TryGetValue(SilentItem, out var value) == true && value == "1";
            BffLog.LoginFailed(logger, settings.Value.Deployment, error ?? "validation_failed", silent);

            context.HandleResponse();
            if (silent && error is not null && InteractionErrors.Contains(error))
            {
                // Silent re-authorization failed: any BFF session the browser still has is unusable and is ended, then
                // an interactive login is required (ADR-017 section 7). The return URL is validated again.
                await context.HttpContext.SignOutAsync(BffSessionDefaults.Scheme);
                var returnUrl = ReturnUrlValidator.Normalize(context.Properties?.Items.TryGetValue(ReturnUrlItem, out var stored) == true ? stored : null);
                context.Response.Redirect(BffPaths.Login + "?returnUrl=" + Uri.EscapeDataString(returnUrl));
                return;
            }

            // Nothing about the failure reaches the browser beyond a generic problem.
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(
                new { type = "about:blank", title = "Sign-in was not completed.", status = 400, detail = "Start the sign-in again." },
                options: null,
                contentType: "application/problem+json");
        }

        private static string? ProtocolError(RemoteFailureContext context)
        {
            var error = context.Request.Query["error"].ToString();
            return error.Length is > 0 and <= 64 && error.All(c => char.IsAsciiLetterLower(c) || c == '_') ? error : null;
        }
    }
}
