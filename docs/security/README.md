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

**Verification gates before any production-like deployment:**

- **Real Safari** (macOS and iOS): login, refresh, logout, cookie attributes and CSRF. Playwright WebKit results do not count as Safari evidence (ADR-016 section 8).
- MFA for platform administrators (Phase 2.2 decision D17, deferred).

Open topics for dedicated design steps:

- signing key and Data Protection key storage outside local development (deployment phase; the direction is in ADR-017)
- tenant isolation enforcement in each service
- secret management outside local development
- TLS for internal service-to-service traffic in deployed environments
