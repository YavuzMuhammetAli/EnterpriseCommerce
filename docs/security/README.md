# Security

Security design documents go here.

Current baseline (Phase 1):

- No secrets in source control. Local infrastructure credentials come from `deploy/docker/.env`, which is gitignored. `.env.example` holds documented, local-only placeholders.
- Local infrastructure ports bind to `127.0.0.1` only.
- Unhandled exceptions return a generic problem details response. Exception messages and stack traces are never sent to clients.
- OpenAPI documents are exposed only in the Development environment.
- NuGet vulnerability auditing (`NuGetAudit`, all packages, including transitive) runs on every restore, and warnings fail the build.
- **Payments are simulated. Card data is never collected, transmitted or stored.**

Accepted security principles ([PROJECT_DECISIONS.md section 10](../architecture/PROJECT_DECISIONS.md#10-security), [ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)):

- Service-level authorization is mandatory, and gateway authorization never replaces it.
- A tenant ID or token claim is not, by itself, proof of access rights.
- Platform administration and merchant authorization are separate concerns.
- Sensitive information is never published in integration events.
- Identity uses OAuth 2.0 and OpenID Connect, implemented with ASP.NET Core Identity and OpenIddict ([ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)):
  - The Resource Owner Password Credentials grant and the implicit flow are rejected.
  - Marketplace authorization is never delegated to the identity provider.
  - Signing keys are never committed.

Accepted authentication and session security (Phase 2.2):

| Topic | Decision |
|---|---|
| Browser clients | A backend-for-frontend per front end. Tokens never reach browser JavaScript. `__Host-Http-` session cookie (`__Host-` fallback), layered CSRF defense, no permissive CORS, POST-only logout, local return URLs, HTTPS everywhere. [ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md) |
| Tokens | RS256 JWT access tokens with `typ: at+jwt`, minimal claims, no role or tenant claims, one audience per front end. Local validation with a 30-second skew. [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) |
| Refresh and revocation | Rotation without leeway, a redemption ledger, presenter-checked reuse detection. The security version and authorization status are the revocation authority. Authoritative changes commit in one short transaction with a whole-transaction deadlock retry. ADR-017 |
| Credential entry | Only on Identity-hosted pages. Confirmed email required. Dedicated account status. Lockout plus progressive delay and IP throttling. ADR-017 |
| Account security tokens | 256-bit random, SHA-256 hashed, single use, consumed atomically by POST only. [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md) |
| Security emails | Delivered by Notification through an authenticated handoff, never through Kafka. The development mail sink has five isolation layers. ADR-018 |
| Routing | Identity on its own host. The Gateway is the only trusted proxy. Forwarded headers are applied before authentication, with `ForwardLimit = 1` and host filtering. ADR-017 |
| Logging | Codes, tokens, verifiers, `id_token_hint`, security tokens and sensitive query strings are never logged. No body or query logging for Identity. CI scans test logs for secret patterns. ADR-017 |

The threat matrix, residual risks and required security tests are in [PHASE_2_2_AUTHENTICATION_FLOWS.md](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#10-failure-and-threat-matrix) (sections 10 and 11).

**Implemented safeguards for security emails (Phase 2.3 S3, ADR-018):** confirmation tokens are minted only after the delivery worker claims a delivery, stored as SHA-256 hashes, consumed only by POST, and never logged. The development mail sink is isolated by five layers: a Debug-only project reference, `#if DEBUG` registration, a runtime guard (Development, explicit opt-in, loopback or the Compose host `mailpit`), a startup failure outside Development if its settings or assembly are present, and CI checks (architecture tests and the Release publish output).

**Implemented browser session security (Phase 2.3 S7, ADR-016, [BFF_API_CONTRACT.md](../api/BFF_API_CONTRACT.md)):**

- **OAuth client.** Authorization code with PKCE `S256` and `response_mode=query`. `private_key_jwt` client authentication (`typ: client-authentication+jwt`). The code exchange is never retried.
- **ID token validation.** Issuer, audience, lifetime, nonce, and a signature check of its own (finding I19). The access-token audience and the presence of a refresh token are verified before any session exists.
- **Server-side sessions.** Sessions live in a dedicated `noeviction` Redis with append-only persistence. The BFF refuses to start against any other policy. The cookie holds only a random 256-bit key, and Redis stores its SHA-256. Tokens are Data Protection-protected in the record and never appear in a browser response. Every login rotates the session ID and deletes the previous session (session fixation, finding I20). Records of another client are rejected. An unreadable store answers `503` (fail closed).
- **Cookies.** `__Host-Http-` session cookie: `Secure; HttpOnly; SameSite=Strict; Path=/`, no `Domain`. Correlation and nonce cookies: `__Host-`, `SameSite=Lax`, at most 5 minutes, deleted at the callback.
- **CSRF.** Fetch Metadata, an exact `Origin` and a session-bound antiforgery token on every state-changing request. No CORS. Local return URLs only.
- **Refresh.** Single-flight across instances through an atomic lease. An unknown outcome is never retried with the same refresh token. `invalid_grant` ends the session.
- **Logout.** Logout revokes at Identity and deletes the session; the end-session URL carries no `id_token_hint` (finding I24). Logout-all is authorized only by the session's bearer token.
- **Logging.** Identity caps `OpenIddict.Validation` at Warning (finding I21). The BFF logs a non-reversible session reference, never a session key, cookie, code, verifier, assertion or token. Tested with Verbose logs over every flow.

**Verification gates before any production-like deployment:**

- **Real Safari** (macOS and iOS): login, refresh, logout, cookie attributes and CSRF. Playwright WebKit results do not count as Safari evidence (ADR-016 section 8). Still open after S7: Playwright's Windows WebKit does not even implement `SameSite` (finding I26).
- The BFF Data Protection key ring in a shared, protected store for multi-instance deployments (D19), and per-deployment Redis ACL users for the session store.
- MFA for platform administrators (Phase 2.2 decision D17, deferred).
- Notification must implement the internal security email endpoint ([IDENTITY_API_CONTRACT.md section 4a](../api/IDENTITY_API_CONTRACT.md#4a-outbound-security-email-handover-to-notification-internal)). Identity refuses to start outside Development until it is configured.

Open topics for dedicated design steps:

- signing key and Data Protection key storage outside local development (deployment phase; the direction is in ADR-017)
- tenant isolation enforcement in each service
- secret management outside local development
- TLS for internal service-to-service traffic in deployed environments
