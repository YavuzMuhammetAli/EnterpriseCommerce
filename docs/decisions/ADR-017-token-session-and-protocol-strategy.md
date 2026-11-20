# ADR-017: Token, Session and Protocol Strategy

- **Status:** Accepted. Amended by [ADR-019](ADR-019-signing-and-encryption-key-rollover-and-retirement.md) (2026-11-20): the key rotation procedure that section 12 refers to (Phase 2.2 section 6.5) is replaced by ADR-019, which also sets how long retired signing and encryption keys are kept. The other decisions of section 12 are unchanged.
- **Date:** 2026-11-18
- **Phase:** 2.2 (authentication flows and security design)
- **Resolves:** "Exact token and session strategy" and "Access token format and resource-server token validation approach" in [PROJECT_DECISIONS.md section 13](../architecture/PROJECT_DECISIONS.md#13-explicitly-deferred-decisions), the direction of "Signing key storage and rotation", and ADR-015 open questions O2, O3, O4, O5, O6, O7, O11 and O12. Decisions D4 to D9, D12, D15, D16, D18, D20, D21 and D24 to D27 of [PHASE_2_2_AUTHENTICATION_FLOWS.md](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#11-decisions-and-implementation-plan).
- **Amends:** [ADR-015](ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md). R1: credential entry is limited to Identity-hosted pages for every credential operation. R4: reuse semantics, the reuse leeway and revocation authority are replaced by section 5 of this ADR. ADR-015's accepted decisions A1 to A8 are unchanged.
- **Related:** [ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md) (BFF), [ADR-018](ADR-018-account-security-tokens-and-security-email-delivery.md) (account security tokens and email), [ADR-014](ADR-014-logical-tenant-isolation.md) (tenancy), [ADR-007](ADR-007-yarp-api-gateway.md) (Gateway).

## Context

ADR-015 selected ASP.NET Core Identity with OpenIddict and left the token, session and protocol details to Phase 2.2. [PHASE_2_2_AUTHENTICATION_FLOWS.md](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md) verified the OpenIddict 7.9.0 and ASP.NET Core Identity 10.0.15 behaviour (V1 to V46), and the owner-run proofs of concept added evidence:

- **PoC-1:** without extra control, two exactly simultaneous refreshes with the same token both succeed and silently fork the session (V41 confirmed). A redemption ledger keyed by the token ID leaves exactly one winner (PoC-1c, PoC-1d). A non-duplicate ledger error must return `503` without revoking (PoC-1e).
- **PoC-2:** an in-flight refresh racing with bulk revocation, authorization revocation or a security-version increment.
- **PoC-3:** private claims are carried through the code and refresh tokens. A custom passthrough runs between native validation and native redemption. The absolute session cap works through the per-principal refresh lifetime (PoC-3c). A missing security-version record fails closed (PoC-3h), and a stale version revokes the authorization (PoC-3i).
- **PoC-13:** the reuse hook. Normal clients revoke only the affected authorization (13a). Admin-console reuse raises the security version and ends every session (13b). A legitimate refresh does not trigger the policy (13c).
  - **PoC-13d:** running the security-version increment and the OpenIddict bulk revocation in **one** large transaction caused SQL Server deadlocks: 13 of 20 trials returned `500`, and one trial left the version unchanged while sessions stayed usable, which **fails open**. Committing the version first, with idempotent cleanup afterwards, passed 20 of 20.
  - **PoC-13e:** a redeemed refresh token presented by a **different** confidential client triggered the reuse policy and revoked the **victim's** authorization. This is a denial-of-service threat. Native OpenIddict validation rejects the request, but only after the reuse handling has run.
- **PoC-12:** a resource server using only the standard ASP.NET Core JWT bearer handler validated OpenIddict tokens and rejected a token for another audience.
- **PoC-14:** Identity behind the Gateway with forwarded headers. With minimal hosting, `UseForwardedHeaders()` must be placed **explicitly before** `UseAuthentication()`. Otherwise OpenIddict serves discovery before the headers are applied, with internal URLs. Untrusted direct requests with spoofed forwarded headers did not change the discovery URLs once the Gateway was the only trusted proxy and host filtering was configured.

## Decision

### 1. Protocol and provider baseline

- **OAuth 2.0 / OpenID Connect** with **ASP.NET Core Identity** and **OpenIddict 7.9.0 or later**. Versions before 7.9.0 are excluded (advisory GHSA-cxgj-hwwh-855g, V18).
- **Authorization code + PKCE (`S256`)** for every interactive client. Client credentials for service clients. ROPC and the implicit flow stay rejected (ADR-015 A4).
- First-party clients get no consent screen. Each login creates a **new ad-hoc authorization**: one authorization is one client session.
- **Sender-constrained tokens: none for the MVP (R0).** DPoP is re-evaluated when OpenIddict supports it. mTLS-bound tokens stay an option for service clients in deployed environments.

### 2. Access tokens

- **Signed, unencrypted JWT**, `RS256` only, with RSA keys of at least 3072 bits. The JOSE header carries **`typ: at+jwt`**, and services require it.
- **Claims (minimal):** `iss`, `sub`, `aud`, `exp`, `iat`, `nbf`, `jti`, `client_id`, `scope`, `amr` (when known), and **`ec_ptype`** (principal type: `user` or `service`).
- **No roles and no tenant context claims.** Platform roles belong to Admin, memberships and merchant roles to Tenant & Merchant, and the tenant is an explicit request parameter validated by the owning service ([ADR-014](ADR-014-logical-tenant-isolation.md)). No personal data (email, name) is put in access tokens.
- Every claim gets an explicit destination list. Private claims used by Identity internally (the security version and the session deadline) never reach access tokens. Services never depend on OpenIddict `oi_*` claims.

### 3. Audiences and client namespaces

- **MVP audiences (Option B): one audience per front-end client.** User access tokens obtained by a BFF carry that front end's audience: `ec-storefront`, `ec-merchant` or `ec-admin`. Each service accepts only the front-end audiences that may call it, and still authorizes every request itself.
- **Service tokens** (client credentials) carry the **audience of the called service**, with scopes for the permitted operations.
- **Per-service user audiences are deferred.** They are revisited when a concrete need appears, for example an API that only one front end may reach for a single operation.
- The **`svc-` client ID prefix is reserved** for service clients. Identity refuses to register or authorize a browser or BFF client with a `svc-` ID, and refuses any grant other than client credentials for a `svc-` client.

### 4. Validation in services and the Gateway

- Each service uses the **standard ASP.NET Core JWT bearer handler**, configured identically. A technical BuildingBlock for this configuration is introduced once a second service needs it.
- Services validate the signature against the cached JWKS, the algorithm allow-list, the exact `iss`, `aud`, `exp` and `nbf`, and `typ`, with a **clock skew of 30 seconds**.
- Services fetch metadata from an **internal Identity metadata address**. The issuer in tokens is still the public issuer URI.
- The Gateway may validate early. It forwards the `Authorization` header unchanged and never replaces service authorization (ADR-015 A5).

### 5. Refresh tokens and reuse

- **Rotation** on every refresh (native). **No reuse leeway** (`SetRefreshTokenReuseLeeway(null)`).
- **Mandatory refresh-token redemption ledger:** a table with a primary key on the refresh token entry ID. The token endpoint inserts the token ID before native redemption. A duplicate key means a reuse. Any other ledger error returns `503` and revokes nothing (PoC-1e).
- **Reuse hook** (an OpenIddict server event handler ordered before `ValidateTokenEntry`):
  1. **Presenter check first.** If the client that authenticated at the token endpoint is not the client the token was issued to, the request is rejected with `invalid_grant`, **nothing is revoked**, and a `RefreshTokenPresenterMismatch` security event is logged. A token from one client can never invalidate another client's session (PoC-13e, threat T24).
  2. **Admin console (`bff-admin`):** reuse raises the user's **security version** and invalidates **every** session of the user.
  3. **Every other client:** reuse revokes **only the affected authorization** (one session).
  4. A security audit event without token material (PoC-13f).
- **Unknown refresh outcomes are never retried with the same refresh token** ([ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md) section 4).
- **Absolute session cap:** the session deadline is stored as a private claim at the code exchange. At each refresh, a custom handler rejects the request after the deadline, and otherwise sets the refresh lifetime to `min(sliding lifetime, deadline - now)` (PoC-3c).

### 6. Revocation authority

- **The authoritative revocation state is the per-user security version plus the authorization status.** A session whose recorded security version is lower than the user's current version cannot refresh, exchange a code or pass authorization, whatever its token entries say.
- **OpenIddict token status is cleanup, not the authority.** Bulk token revocation keeps listings and storage clean, but correctness never depends on it completing.
- Access tokens already issued stay valid until they expire. That is the accepted trade-off of local validation, bounded by short lifetimes.

### 7. Security version

- A **dedicated per-user security-version record** (a counter). It is **not** the ASP.NET Core Identity security stamp, which also changes for reasons that are not security events (V34).
- It is checked on Identity cookie validation, at the authorization endpoint, at the code exchange and at every refresh.
- **A missing record fails closed** (PoC-3h).
- **Database unavailable:** the request fails with `503`. The Identity cookie is **not** silently cleared.
- **Raised by:** password reset, logout-all, account disablement, and admin-console refresh-token reuse.
- **Password change** revokes the user's other sessions and **reissues the current browser login cookie** with the new version, so that the current session continues.

### 8. Transactions and deadlocks

**Authoritative transaction.** The following commit together in **one short transaction**:

- the account change, where applicable (password hash, email confirmation, status)
- the consumption of a single-use security token ([ADR-018](ADR-018-account-security-tokens-and-security-email-delivery.md))
- the security-version increment
- the relevant **single** authorization state change (for example revoking the session in which reuse occurred)

**Deadlock handling:**

- SQL Server error **1205** causes a **complete transaction retry** with a fresh unit of work. Individual statements are never retried inside an aborted transaction.
- The retry count is bounded and uses backoff.
- When retries are exhausted, the request fails, **no token is issued**, and a structured log entry plus an operational metric are emitted.

**Post-commit cleanup:**

- OpenIddict **bulk** token and authorization revocation runs **after** the authoritative transaction commits.
- Cleanup is idempotent. Cleanup deadlocks are retried separately.
- Cleanup failure never reverses or misrepresents the committed security decision. The security version and the authorization status remain the revocation authority.

This model is the revised D9/D24, chosen because of PoC-13d.

### 9. Credential entry and the account UI

- **Every credential entry happens on Identity-hosted pages:** login, registration, password change, password reset confirmation and, later, MFA. This clarifies the scope of ADR-015 R1 (D27).
- No client application or BFF receives a password. The password-bearing JSON endpoints of the Phase 0.3 baseline are retired ([IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md)).
- A **confirmed email is required** before sign-in for every account (D12).
- **Account disablement** uses a dedicated account status, never lockout.
- **Lockout:** 5 failures, 15 minutes. **Progressive per-account delay and per-IP throttling** on the login, registration, resend and reset pages.
- The Identity SSO cookie uses the `__Host-` prefix, `Secure`, `HttpOnly` and `SameSite=Lax` (it must survive top-level navigations from front-end hosts). Every Identity form is antiforgery protected, with Fetch Metadata and `Origin` checks on POST.

### 10. Identity routing and forwarded headers

- Identity has **its own host name** per environment, routed by host through the Gateway without a path prefix. The issuer, discovery URLs and browser URLs match without rewriting.
- **The YARP Gateway is the only trusted proxy.**
- Forwarded headers are applied **explicitly before the authentication middleware** (PoC-14).
- Only the Gateway's address is trusted (`KnownProxies` or `KnownIPNetworks` on .NET 10), with **`ForwardLimit = 1`**.
- **Host filtering** is configured. A direct request with an untrusted `Host` must not influence discovery URLs.

### 11. Logging

- `Microsoft.AspNetCore`, `Yarp` and `OpenIddict.Server` log at **Warning** or higher.
- **Never logged:** authorization codes, access, refresh and ID tokens, confirmation and password-reset tokens, `code_verifier`, `id_token_hint`, client assertions, passwords, or sensitive query strings.
- No HTTP request or response body or query logging for Identity. Request logs record the path only.
- OpenTelemetry query redaction stays on when tracing is introduced.
- **CI scans test logs** for known secret patterns.

### 12. Lifetimes, signing keys and pruning

- **Initial configured lifetimes** are the values in [PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.4](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#64-proposed-lifetimes) (for example 5-minute admin access tokens and 60-second authorization codes). They are configuration, tunable within this ADR without a new decision. The policy (short access tokens, sliding refresh capped by an absolute lifetime) is the decision.
- **Signing and encryption keys:** separate RSA certificates, distinct from TLS, never committed. Development uses generated, non-committed keys. Deployed environments load certificates from a protected store. Identity refuses to start outside Development without them. Rotation follows section 6.5 of the Phase 2.2 document. The concrete deployed store is deferred to the deployment phase.
- **Pruning** of redeemed, revoked and expired entries uses a **hosted service that calls the OpenIddict prune APIs**. No `OpenIddict.Quartz` or Quartz.NET dependency is added (D18).

## Consequences

**Positive:**

- The session-fork race (V41) and the deadlock fail-open case (PoC-13d) are closed by explicit, tested mechanisms.
- Cross-client replay cannot be used to sign other clients' users out.
- Revocation correctness rests on one short transaction and two authoritative values, independent of OpenIddict cleanup.
- Services stay independent of OpenIddict packages.
- Claims stay minimal, so there are no stale role or tenant claims.

**Negative and accepted:**

- Custom code on the security path: the ledger, the reuse hook, the security version, the token handler and the retry runner. All of it needs concurrency, rollback and failure tests.
- Admin-console users lose every session when admin-console reuse is detected.
- An access token stays valid for up to its lifetime after revocation.
- Every BFF hop and every service must be configured for front-end audiences. Finer per-service user audiences are deferred.

**Required implementation tests** (listed with their owners in [PHASE_2_2_AUTHENTICATION_FLOWS.md section 11.5](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#115-required-implementation-tests)): concurrent refresh and ledger uniqueness, reuse from the correct presenter, cross-client replay rejection, admin-console reuse with all-session invalidation, deadlock retry and retry exhaustion, post-commit cleanup failure, password reset or logout-all concurrent with refresh, forwarded headers before authentication, host-header spoofing, and sensitive-log redaction.

## Alternatives considered

- **A reuse leeway (default 30 seconds):** it creates valid sibling refresh tokens, and it is global. Rejected.
- **No leeway without a ledger:** exact races still fork sessions silently (V41, PoC-1b). Rejected.
- **One large transaction that includes OpenIddict bulk revocation:** deadlocks and can fail open (PoC-13d). Rejected.
- **The security stamp as the revocation authority:** it changes on non-security events (login rehash, email change). Rejected.
- **Token status as the revocation authority:** bulk updates can deadlock or fail partway. Rejected as authority, kept as cleanup.
- **Role and tenant claims in access tokens:** stale, and not proof under ADR-014. Rejected.
- **A shared API audience for all user tokens:** weaker separation between front ends. Rejected for the MVP.
- **Per-service user audiences now:** more client and scope configuration without a present need. Deferred.
- **The OpenIddict validation handler in every service:** couples every service to OpenIddict. Rejected.
- **Encrypted access tokens (JWE):** spreads the decryption key to every service. Rejected (ADR-015 R3).
- **Path-prefix routing of Identity (`/identity`):** issuer and URL mismatches. Kept only for development diagnostics.
