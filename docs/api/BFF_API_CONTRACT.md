# BFF API Contract

The browser-facing endpoints of the backend-for-frontend ([ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md), [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md)). One codebase serves three deployments (Storefront, Merchant Portal, Admin Console). Each deployment has its own host name, OAuth client, cookies, Data Protection key ring and Redis key namespace. The browser calls only same-origin `/bff/*` and `/api/*`.

**Status:** implemented in Phase 2.3 slice S7. Real Safari (macOS, iOS) is not yet verified ([section 7](#7-verification-status)).

## 1. Conventions

- **Tokens.** No endpoint ever returns an access token, refresh token, ID token or authorization code. Tokens exist only in the server-side session.
- **Errors.** Errors are RFC 9457 problem details without internal detail. `401` carries `WWW-Authenticate: BffSession login="/bff/login"`.
- **Caching.** Every `/bff/*` and `/api/*` response is `Cache-Control: no-store`.
- **Headers.** Every response has `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY` and `Cross-Origin-Opener-Policy: same-origin`. HSTS is sent outside Development.
- **CORS.** None. No `Access-Control-*` header is ever sent.
- **State-changing requests.** These are `POST`, `PUT`, `PATCH` and `DELETE` to `/bff/*` or `/api/*`. All of the following must pass, otherwise `403` before anything runs or is forwarded:
  1. `Sec-Fetch-Site: same-origin`. If Fetch Metadata is absent, an exact `Origin` match with the deployment's public origin. `same-site`, a missing `Origin` and `null` are rejected.
  2. A valid session (`401` otherwise).
  3. `X-XSRF-TOKEN` equal to the session-bound antiforgery request token. A new session needs a new token.
- **Temporary failures.** An unreachable session store or Identity gives `503` with `Retry-After`. The request fails closed: it is never answered as authenticated, and a session is never created on partial state.

## 2. Cookies (per deployment; `{d}` is the deployment name)

| Cookie | Attributes | Content |
|---|---|---|
| `__Host-Http-ec-{d}` (session) | `Secure; HttpOnly; SameSite=Strict; Path=/`, no `Domain` | A random 256-bit key. A browser-session cookie; persistent only for Storefront "keep me signed in" (`persistent=1`), expiring with the absolute lifetime. A deployment can fall back to `__Host-ec-{d}` (`Bff:Cookies:UseHttpOnlyPrefix=false`). |
| `__Host-ec-{d}-oidc-c.*`, `__Host-ec-{d}-oidc-n.*` (correlation, nonce) | `Secure; HttpOnly; SameSite=Lax; Path=/`, at most 5 minutes | Data Protection-protected OAuth correlation data. Deleted at the callback. |
| `__Host-ec-{d}-af` (antiforgery cookie token) | `Secure; HttpOnly; SameSite=Strict; Path=/` | ASP.NET Core antiforgery. |
| `__Host-ec-{d}-xsrf` (antiforgery request token) | `Secure; SameSite=Strict; Path=/`, readable by script | Echoed by the front end in `X-XSRF-TOKEN`. |

## 3. Endpoints

### 3.1 `GET /bff/login`

Starts a sign-in with a full-page navigation.

| Query parameter | Meaning |
|---|---|
| `returnUrl` | A local path. Anything else (absolute, protocol-relative, backslash or encoded variants, `/bff/*`) becomes `/`. |
| `silent=1` | Try `prompt=none` first, through Identity's SSO cookie. |
| `persistent=1` | "Keep me signed in", honoured only where the deployment allows it. |

The response is a `302` to Identity's authorization endpoint with:
- `response_type=code` and `response_mode=query`
- `code_challenge_method=S256`
- `scope=openid offline_access`
- the registered `redirect_uri` from configuration, never from the request host
- single-use `state` and `nonce`

### 3.2 `GET /bff/callback`

The registered redirect URI. It is handled by the BFF only.

**Accepted response.** The handler validates the correlation cookie and the `state`. It then exchanges the code once with `private_key_jwt` and the PKCE verifier; the exchange is never retried. Then it validates the ID token: issuer, audience (client ID), lifetime, nonce, and a signature from the issuer's JWKS. It also checks that the access token's audience is this front end's audience and that a refresh token is present. Only then does it create a new session; the session the request arrived with is deleted. The browser is redirected to the validated return URL.

**Failure.** The response is `400` problem details ("Sign-in was not completed"), and no session is created.

**Silent failure.** When a `silent=1` attempt returns `login_required`, `interaction_required`, `consent_required`, `account_selection_required` or `access_denied`, the BFF ends any BFF session the browser still has. It then redirects to `GET /bff/login?returnUrl=…` without `silent`, which is an interactive login.

### 3.3 `GET /bff/user`

`200 {"sub": "...", "amr": ["pwd"], "sessionExpiresAt": "..."}`. It also issues a fresh `__Host-ec-{d}-xsrf` request token.

It checks that the session can still obtain a valid access token, refreshing if one is due ([section 4](#4-session-and-refresh-behaviour)).

| Response | Meaning |
|---|---|
| `401` | The session is missing, has expired, or has ended (for example after `invalid_grant`). The front end then navigates to `/bff/login?silent=1&returnUrl=…`. |
| `503` | Temporarily unavailable. |

### 3.4 `POST /bff/logout`

1. The BFF revokes the refresh token at Identity's `/connect/revoke`, and Identity ends the whole authorization. This step is best effort: a failure is logged and the logout continues.
2. It deletes the session. This is the authoritative step: if it fails, the request answers `503` and the cookie stays.
3. It expires the session and request-token cookies.

**Response:** `200 {"signedOut": true, "endSessionUrl": "…"}`.

`endSessionUrl` is Identity's end-session endpoint with `client_id` and the registered `post_logout_redirect_uri`. It deliberately carries **no** `id_token_hint`, which would put the ID token into the browser (finding I24). The front end navigates to it; Identity shows its confirmation (finding I17), and its POST ends the SSO cookie.

### 3.5 `POST /bff/logout-all`

The BFF obtains a valid access token for the session (refreshing if due). It then calls `POST /api/v1/auth/logout-all` at Identity with it as a bearer token. The user is identified only by that token; no browser-supplied identifier is used.

| Identity's answer | BFF response |
|---|---|
| `204` | The local session ends as in [3.4](#34-post-bfflogout). `200 {"signedOut": true, "endSessionUrl": "…"}`. Every other session of the user, in any browser or front end, ends at its next refresh (`invalid_grant`). |
| `401` or `403` | `401` problem. Success is not claimed and the session is kept. |
| `503`, or Identity unreachable | `503` and the session is kept: retry. |
| (no usable access token; the session ended) | `401`. |

### 3.6 `GET /bff/signed-out`

The registered post-logout redirect URI. It redirects to the deployment's `PostLogoutLandingPath`.

### 3.7 `/api/{**path}`

Forwarded to the configured Gateway address (`Bff:DownstreamApiAddress`) with `Authorization: Bearer <session access token>`, refreshed if due.

- **Removed from the request:** the browser's `Cookie` header, `X-XSRF-TOKEN` and any incoming `Authorization` header.
- **Removed from the response:** downstream `Set-Cookie` headers.
- **Without a session:** `401` and nothing is forwarded.

Authorization stays with the downstream services.

### 3.8 `GET /health`

Healthy only while the session Redis answers and uses `noeviction`.

## 4. Session and refresh behaviour

- **Storage.** The session lives in the dedicated session Redis under `{prefix}:s:{sha256(cookie key)}`.
  - The default prefix is `bff:{deployment}`.
  - The record holds the client ID, the protected principal, the Data Protection-protected token set, the refresh state and lease, and the deadlines.
  - A record with another client ID is rejected.
- **Expiry.** The idle timeout slides with use, but never past the absolute lifetime. Both are enforced by the record's Redis TTL.

| Deployment | Absolute | Idle | Persistent cookie |
|---|---|---|---|
| Storefront | 30 days | 7 days | Only with `persistent=1` |
| Merchant Portal | 12 h | 2 h | No |
| Admin Console | 8 h | 30 min | No |

- **When refresh happens.** Refresh starts when the access token is within min(2 minutes, a third of its lifetime) of expiry, and only on `/bff/user`, `/bff/logout-all` and `/api/*`.
- **Single-flight.** One refresh per session across all instances: an atomic lease in the record (Lua compare-and-set, 15 seconds). Only the lease owner presents the refresh token or stores the result. Others wait up to 6 seconds for the result, then use the still-valid access token or answer `503`.

| Refresh outcome | Behaviour |
|---|---|
| Success | The new token set replaces the old one atomically. |
| `invalid_grant` | The session is deleted; the next response is `401`. Causes include revocation, reuse, a password change and logout-all. |
| `invalid_client`, `unauthorized_client` | Never retried. Logged as an error (alert). The session continues until the access token expires, then ends. |
| Unknown: timeout after sending, connection reset, `5xx`, or a lease that expired without a result | The refresh token is never presented again. The session continues until the access token expires, then ends. |
| Provably not sent (the connection could not be established) | The token is still unused. A later request retries. |

## 5. Deployment configuration (`Bff` section)

Exactly one front end per instance. Startup fails on any invalid value, without a session Redis connection string, or if the session Redis is unreachable or not `noeviction`.

| Key | Meaning |
|---|---|
| `Deployment` | `storefront`, `merchant` or `admin`. It derives the cookie names, Redis prefix and Data Protection application name. |
| `ClientId`, `Audience` | `bff-{d}` and `ec-{d}`. The `svc-` prefix is refused. |
| `Authority` | The Identity issuer (HTTPS, trailing `/`). |
| `PublicOrigin` | The front end's origin. The redirect URIs and the CSRF origin check derive from it. |
| `ClientKey:PrivateKeyPath`, `ClientKey:KeyId` | The `private_key_jwt` key: a mounted secret. `PrivateKeyPem` is accepted only in Development. |
| `Session:AbsoluteLifetime`, `Session:IdleTimeout`, `Session:AllowPersistent` | See [section 4](#4-session-and-refresh-behaviour). |
| `Refresh:Threshold`, `RequestTimeout`, `LeaseDuration`, `WaitForOtherRefresh` | 2 min, 5 s, 15 s, 6 s. |
| `DataProtectionKeyDirectory` | Required outside Development and shared by one deployment's instances. The production key store is deferred (D19). |
| `RedisKeyPrefix` | Optional. Defaults to `bff:{d}`. |
| `DownstreamApiAddress` | Optional. The Gateway address for `/api/*`. |
| `Proxy:KnownProxies`, `Proxy:KnownNetworks`, `Proxy:PublicHosts` | The trusted Gateway (`ForwardLimit=1`). Required, with explicit `AllowedHosts`, outside Development. |
| `ConnectionStrings:BffSessions` | The dedicated session Redis. |

## 6. Identity registration per deployment

| Setting | Value |
|---|---|
| Client | Browser kind, confidential (`private_key_jwt` with the public key in `JsonWebKeySet`) |
| Redirect URI | `{PublicOrigin}/bff/callback` |
| Post-logout redirect URI | `{PublicOrigin}/bff/signed-out` |
| Audience | `ec-{d}` |
| Lifetimes | As in [section 4](#4-session-and-refresh-behaviour) |

`deploy/dev/bff-dev-keys.cs` generates the local development keys and registrations.

## 7. Verification status

| Evidence | Covers |
|---|---|
| Automated integration tests (real Identity, SQL Server, session Redis) | Every behaviour above, including concurrency across two instances, unknown refresh outcomes, Redis outage and restart, and `noeviction` enforcement |
| Automated browser tests (Playwright: Chromium, Firefox, WebKit) | Login, cookie attributes, script visibility, cross-site POST, logout, idle expiry, redirect validation, password-change recovery |
| **Not verified** | Real Safari on macOS and iOS (the ADR-016 gate); the full local flow through the Gateway on a machine whose development certificate is not trusted (finding I29) |
