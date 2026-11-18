# ADR-016: Browser Client Architecture: Backend-for-Frontend

- **Status:** Accepted
- **Date:** 2026-11-18
- **Phase:** 2.2 (authentication flows and security design)
- **Resolves:** "Browser client architecture" in [PROJECT_DECISIONS.md section 13](../architecture/PROJECT_DECISIONS.md#13-explicitly-deferred-decisions), and ADR-015 open question O1 (with R2). Decisions D1, D2, D3, D22 and D23 of [PHASE_2_2_AUTHENTICATION_FLOWS.md](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#11-decisions-and-implementation-plan). It supersedes no earlier ADR.
- **Related:** [ADR-017](ADR-017-token-session-and-protocol-strategy.md) (tokens and sessions), [ADR-007](ADR-007-yarp-api-gateway.md) (Gateway), [ADR-006](ADR-006-redis-usage-boundaries.md) (Redis).

## Context

EnterpriseCommerce has three Angular front ends: the customer storefront, the merchant portal and the admin console. ADR-015 accepted OAuth 2.0 and OpenID Connect with OpenIddict, and left open where browser tokens live (O1).

Inputs:

- **RFC 10017** (OAuth 2.0 for Browser-Based Applications, BCP, August 2026) strongly recommends a backend-for-frontend (BFF) for business applications. It does not recommend browser-only OAuth clients for them.
- OpenIddict 7.9.0 has no DPoP support, so refresh tokens in a browser would be pure bearer tokens ([PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.6](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#66-sender-constrained-tokens)).
- The admin console and merchant portal carry high privileges. The storefront has the largest user population and the most third-party content.
- The Phase 2.2 proofs of concept measured cookie-prefix, SameSite and Fetch Metadata behaviour in Chromium, Firefox and Playwright WebKit (PoC-9), and Redis lease and eviction behaviour (PoC-11). Playwright WebKit is not Safari, so these runs do not prove real Safari behaviour.

The comparison of the public SPA client, the token-mediating backend and the BFF is in [PHASE_2_2_AUTHENTICATION_FLOWS.md section 4](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#4-browser-client-architecture).

## Decision

### 1. Every browser application uses a BFF

- The storefront, the merchant portal and the admin console each use a **BFF**. No browser application is an OAuth client, and no token ever reaches browser JavaScript.
- **One BFF codebase, deployed once per front end** (topology T1). Each deployment is configured for exactly one front end and refuses to start with more than one client configuration.
- Each deployment has its own:
  - OAuth client (`client_id`) and credentials
  - host name
  - cookie names and cookie configuration
  - scopes and audiences ([ADR-017](ADR-017-token-session-and-protocol-strategy.md): one audience per front end)
  - ASP.NET Core Data Protection application name and key ring
  - Redis key namespace
- Each session record stores its `client_id`, and a BFF rejects any record that carries another one.
- **Placement:** every BFF sits **behind the YARP Gateway**, which routes each front-end host to its BFF. The Gateway stays the single external entry point ([ADR-007](ADR-007-yarp-api-gateway.md)). The Gateway holds no sessions or tokens.
- The BFF **serves its front end's compiled static files** and forwards API calls **through the Gateway**, adding `Authorization: Bearer <access token>`.
- **No business logic in a BFF.** It handles sessions, tokens and forwarding only, and references no service project.
- The BFF is a technical deployable, not a business microservice. The rule of exactly ten business services is unchanged.

### 2. OAuth client behaviour

- Authorization code flow with **PKCE `S256`**, for every BFF client.
- Each BFF is a **confidential client**. Deployed environments use **`private_key_jwt`** client authentication with an asymmetric key from a secret store. Local development may use a development key or secret from user configuration, never from the repository.
- `state` and `nonce` are single-use and validated. The authorization response uses `response_mode=query`.
- **Return URLs are local paths only.** Identity accepts only exactly registered redirect and post-logout redirect URIs.
- **Logout is POST only.**

### 3. Session storage

- **Server-side session tickets.** The cookie carries only an opaque session reference. The tokens live in the session store with a TTL equal to the remaining absolute session lifetime.
- The store is a **dedicated Redis instance** for BFF sessions:
  - `maxmemory-policy noeviction`, so that sessions are never silently evicted under memory pressure (PoC-11 R4)
  - never shared with ordinary cache workloads
  - **startup validation:** a BFF refuses to start if the instance's eviction policy is not `noeviction`
- Losing session data only signs users out. It never corrupts business data. This fits ADR-006 (Redis is never a system of record).

### 4. Refresh coordination

- Refresh starts ahead of expiry. Requests arriving during a refresh keep using the still-valid access token.
- **Distributed single-flight:** one refresh per session across all instances, through an **atomic lease** in the session record (compare-and-set in one Redis script). Only the lease owner may store the new tokens (PoC-11 R2).
- **An unknown refresh outcome is never retried with the same refresh token.** That covers a timeout after sending, a connection reset, any `5xx` from Identity, and an expired lease without a result (PoC-11 R3). The session continues until the access token expires, then ends. Retrying is allowed only when the request was provably never sent.
- `invalid_grant` ends the BFF session.

### 5. Cookies

| Cookie | Name prefix | Attributes |
|---|---|---|
| BFF session | `__Host-Http-` where the browser supports it. A compatibility fallback uses `__Host-` where required. | `Secure`, `HttpOnly`, `SameSite=Strict`, `Path=/`, no `Domain`, all set **explicitly** |
| OAuth correlation (`state`, `nonce`, PKCE verifier) | `__Host-` | `Secure`, `HttpOnly`, **`SameSite=Lax`**, a lifetime of a few minutes, deleted at the callback |
| Antiforgery request token (readable by Angular) | `__Host-` | `Secure`, `SameSite=Strict`, bound to the session |

Every attribute is set explicitly, because browsers without prefix support accept prefixed cookies without the prefix guarantees.

### 6. CSRF

A state-changing request is forwarded only if **every** layer passes. Otherwise it gets `403` before anything is forwarded:

1. `SameSite=Strict` session cookie.
2. **Fetch Metadata:** `Sec-Fetch-Site` must be `same-origin`. If it is absent, `Origin` must **exactly** match the front end's origin. A missing or `null` `Origin` without Fetch Metadata is rejected.
3. A **session-bound antiforgery token** in a request header (`X-XSRF-TOKEN`).
4. **No permissive CORS** on any BFF: no `Access-Control-Allow-*` header for any origin.

Safe methods never change state.

### 7. HTTPS

- **HTTPS everywhere, including local development** (the ASP.NET Core development certificate), with HSTS on deployed browser-facing hosts.
- The BFF trusts `X-Forwarded-*` only from the Gateway.

### 8. Browser verification gate

- Chromium, Firefox and Playwright WebKit runs (PoC-9) are evidence for those engines only. **Playwright WebKit does not prove real Safari behaviour.**
- Safari does not enforce the `__Host-Http-` prefix at the time of writing. The design does not depend on it, because every attribute is set explicitly and the CSRF layers do not rely on the prefix.
- **Gate:** before any production-like deployment, the login, refresh, logout, cookie-attribute and CSRF flows must be verified manually or with device automation in **real Safari** on macOS and iOS. The result is recorded in the security documentation. Until then, Safari support is an open residual risk.

## Consequences

**Positive:**

- XSS in a front end can act only through the open session while the page is open. It cannot take tokens away.
- Refresh tokens stay on the server and are bound to confidential clients, so a stolen cookie alone cannot redeem them.
- Process-level isolation between front ends: a compromise of the storefront BFF exposes no admin credential or session.
- The front ends contain no OAuth library and no token handling.

**Negative and accepted:**

- One extra deployable per front end, and one extra internal hop (BFF, then Gateway) per API call.
- A dedicated Redis instance for sessions. Locally it runs only when a BFF is being worked on.
- An unknown refresh outcome causes a forced sign-in at access token expiry. This is measured (an `uncertain refresh` metric).
- Real Safari behaviour stays unverified until the browser gate is run.

**Implementation constraints:**

- The BFF project location, its Compose profile and its container image are decided in the BFF implementation slice of Phase 2.3. Architecture tests are extended so that the BFF, like the Gateway, references no service project.
- The dedicated session Redis is a separate Compose service with `noeviction`. The existing `redis` service keeps `allkeys-lru` for caching.

## Alternatives considered

- **Public SPA client for the storefront:** acceptable only under strict conditions (5-minute in-memory access tokens, rotated refresh tokens, strict CSP, cross-tab coordination). Rejected: without DPoP, browser refresh tokens are bearer tokens, and RFC 10017 does not recommend this pattern for business applications.
- **Token-mediating backend:** the operational cost of a backend with most of the token exposure of a public client. Rejected.
- **Three separate BFF codebases (T2):** three copies of security-critical code to keep in step. Rejected.
- **One shared BFF process for three hosts (T3):** isolation would depend on configuration correctness alone. Rejected.
- **BFF functionality inside the Gateway:** the Gateway holds no sessions or tokens ([ADR-007](ADR-007-yarp-api-gateway.md)). Rejected.
- **Encrypted cookie that carries the tokens:** large, chunked and impossible to revoke on the server. Rejected.
- **Shared Redis with `allkeys-lru`:** sessions would be evicted silently under memory pressure (PoC-11 R4). Rejected.
