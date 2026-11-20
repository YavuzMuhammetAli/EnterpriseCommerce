# Phase 2 Design Brief: Identity

- **Phase type:** design (Phases 2.1 and 2.2), then implementation (Phase 2.3).
- **Status:**
  - **Phase 2.1 (provider selection) is complete:** ASP.NET Core Identity with OpenIddict, recorded in [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md).
  - **Phase 2.2 (authentication flows and security design) is complete:** approved on 2026-11-18 and recorded in [ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md) (BFF), [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) (tokens, sessions, protocol) and [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md) (account security tokens and email). Rationale, residual risks and the test plan: [PHASE_2_2_AUTHENTICATION_FLOWS.md](PHASE_2_2_AUTHENTICATION_FLOWS.md#11-decisions-and-implementation-plan).
  - **Phase 2.3 (Identity implementation) is authorized and in progress.** It implements the accepted ADRs in small vertical slices.
- **Exit gate:** the design exit gate was passed with the Phase 2.2 approval.

This brief defines the scope, questions and evaluation criteria for designing the Identity microservice.

- **Decided in Phase 2.1:** the provider and the protocol family (OAuth 2.0 and OpenID Connect).
- **Still outputs of the remaining Phase 2 steps:** the client flows, the token strategy and the data model. [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md) lists the recommendations and open questions for them.

Inputs: [PROJECT_DECISIONS.md](PROJECT_DECISIONS.md) (especially sections 5, 7 and 10), [SERVICE_CATALOG.md](SERVICE_CATALOG.md), [ADR-007](../decisions/ADR-007-yarp-api-gateway.md) and [ADR-014](../decisions/ADR-014-logical-tenant-isolation.md).

## Phase 2.3 implementation progress

| Slice | Content | Status |
|---|---|---|
| S1 | Persistence, migrations, authoritative transaction with whole-transaction deadlock retry, post-commit cleanup | Done |
| S2 | Identity-hosted login, authorization code + PKCE, token issuance, redemption ledger, presenter-checked reuse policy | Done |
| S3 | Registration, email confirmation, resend limits, security email delivery records, bounded queue, worker, status-only reconciliation, Notification adapter, isolated development mail sink | Done |
| S5 | Gateway routing and trusted forwarded headers | Done |
| S6 | First part: log redaction guard, health checks, CI secret scan. Second part: account disablement and reactivation (internal API for Admin), retention cleanup of the redemption ledger and OpenIddict entries, signing and encryption key rollover with explicit key states, Data Protection key rings encrypted at rest, readiness checks, bounded-tag metrics. Operations: [IDENTITY_OPERATIONS.md](../security/IDENTITY_OPERATIONS.md) | Done (production key provider and the open retention decision are deployment gates) |
| S4 | Token revocation and end-session (POST-only browser logout), logout-all endpoint, password reset and change, deterministic reset-versus-refresh and logout-all-versus-refresh race tests | Done |
| S7 | Backend-for-frontend and the dedicated session Redis: one codebase (`src/Bff`), three deployments; authorization code + PKCE + `private_key_jwt`; server-side sessions in a `noeviction` Redis; single-flight refresh; CSRF layers; logout and logout-all; silent re-authorization after a password change; Playwright browser tests. Contract: [BFF_API_CONTRACT.md](../api/BFF_API_CONTRACT.md) | Done (real Safari gate open) |

**Not verified by S4:** real browsers (the tests drive HTTP directly), delivery of reset emails through Notification (not implemented), and SQL Server versions other than 2022.

**Verified by S6:**

- Account disablement forced against refresh, code exchange, password reset and a second disablement (both orderings each), and reactivation against login, on SQL Server 2022.
- Retention against real locks and two concurrent instances.
- Key rollover across Identity restarts with persisted certificates, validated by the standard JWT bearer handler, Identity's own validation and the BFF.

**Not verified by S6:**

- A KMS or HSM key provider (none is implemented), or an accepted certificate-file alternative (not decided).
- A shared Data Protection volume and multi-instance rolling restarts in a production-like environment. Rotating the Data Protection key-encryption certificate is not implemented.
- The Admin service, which does not exist yet; a test client stands in for it. Administrator MFA (D17) is deferred.
- Notification's security email endpoint, a metrics exporter and monitoring.

The full list of gates is in [IDENTITY_OPERATIONS.md section 7](../security/IDENTITY_OPERATIONS.md#7-verification-level-and-remaining-production-gates). The key rollover policy is [ADR-019](../decisions/ADR-019-signing-and-encryption-key-rollover-and-retirement.md).

**Verified by S7:** the BFF side of logout, logout-all and re-authorization after a password change, in integration tests and in Chromium, Firefox and WebKit (Playwright). **Not verified by S7:** real Safari on macOS and iOS (the ADR-016 gate before any production-like deployment), and the full local flow through the Gateway on a machine without a trusted development certificate (finding I29).

The brief for this slice called it "S5". It is S7 in this plan, because S5 (Gateway routing) was already done.

Implementation findings are recorded in [PHASE_2_2_AUTHENTICATION_FLOWS.md section 11.7](PHASE_2_2_AUTHENTICATION_FLOWS.md#117-implementation-findings-phase-23).

## Prerequisites

1. **API contract baseline.** The Phase 0.3 Identity API baseline is preserved in [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md). Its endpoint areas, conventions and claim set are the baseline. Exact paths and fields are marked *Reconstructed* and must be confirmed against the original Phase 0.3 design during Phase 2 review.
2. Confirm the client types in scope for the MVP (see 7.1). The answer drives the protocol choice.

## 7.1 Authentication protocol

**Questions to answer:**

- Which standards are needed, and why:
  - **OAuth 2.0** (delegated authorization: access tokens for APIs)
  - **OpenID Connect** (an authentication layer on OAuth 2.0: ID tokens, standard discovery and user info)
  - **JWT** as the access-token format, versus opaque tokens with introspection
- Which client types must be supported, and which flow each needs:
  - a browser single-page app or server-rendered web front end (authorization code + PKCE; backend-for-frontend option)
  - mobile or native apps, if planned
  - API and third-party clients
  - Swagger/OpenAPI tooling during development
- Whether the Resource Owner Password flow is acceptable. It is deprecated in OAuth 2.1 guidance, and its use must be justified or rejected explicitly.
- **Service-to-service authentication:** client credentials, workload identities or mTLS, and how services validate tokens locally (JWKS, issuer and audience checks).
- How the Gateway validates tokens without duplicating authorization logic ([ADR-007](../decisions/ADR-007-yarp-api-gateway.md)).

**Evaluation criteria:** standards compliance, security, client compatibility, operational complexity, and fit with the YARP Gateway and per-service authorization.

## 7.2 Identity provider options

> **Outcome (Phase 2.1):** **ASP.NET Core Identity with OpenIddict** is selected, per [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md). The ADR records the verified facts, the responsibility boundaries and the rejected alternatives. The table below is kept as the original, pre-decision candidate list.

When this brief was written, the candidates below were **not ranked and none was selected**. Facts must be re-verified against official sources during Phase 2. The notes reflect what was publicly documented at the time of writing.

| Option | What it is | Notes to verify in Phase 2 |
|---|---|---|
| **ASP.NET Core Identity** (alone) | User, credential, password hashing, lockout and 2FA store. **Not** an OAuth 2.0 / OIDC server. | Its built-in Identity API endpoints issue bearer tokens that are **not JWTs** (Data Protection-based, proprietary format), which limits interoperability. It is likely to be a building block *combined with* a protocol server, rather than a full solution. |
| **OpenIddict** | An OAuth 2.0 / OIDC server framework for ASP.NET Core, usually combined with ASP.NET Core Identity for the user store. | Apache-2.0 licensed. Maintained mainly by one lead maintainer, so bus factor should be assessed. Current version and .NET 10 support to be confirmed. More assembly required than a turnkey product. |
| **Duende IdentityServer** | A mature, OpenID-certified OAuth 2.0 / OIDC framework. | **Commercial licence.** A free Community Edition exists for qualifying small organizations. Current thresholds and terms to be confirmed. The licensing implications for a public portfolio project must be evaluated. |
| **External / standalone identity server** (for example Keycloak, a separately deployed Java service) | A full identity and access management product, run as its own container. | It would add a non-.NET runtime and a new infrastructure component. That is a **technology change requiring an ADR and approval**. |
| **Managed identity platform** (for example Microsoft Entra External ID or Auth0) | A hosted identity provider. | It moves credentials out of the platform, which changes Identity's ownership scope. It introduces a cloud dependency, cost, and offline local-development constraints. Also a technology change. |

**Comparison criteria (all options):**

- security track record and patch cadence
- standards compliance and certification
- extensibility (custom claims, tenant context, custom grant types)
- licensing and cost (including for a public repository)
- maintenance outlook and community
- complexity
- integration with SQL Server and EF Core, Clean Architecture and the YARP Gateway
- testability
- local-development experience
- long-term ownership and lock-in

Do not pick the simplest or most popular option by default. The chosen option must be justified in an ADR with these trade-offs.

## 7.3 Identity responsibilities

| Identity | Tenant & Merchant | Admin |
|---|---|---|
| User identity | Tenant lifecycle | Platform administrator assignments |
| Credentials | Merchant lifecycle | Platform governance |
| Authentication | Membership | Administrative policies |
| Sessions | Invitations | Audit |
| Token lifecycle | Merchant roles | |

Questions:

- How membership and role data from Tenant & Merchant and Admin reach token issuance, if at all: embedded claims versus lookups at authorization time.
- How invitations (owned by Tenant & Merchant) link to account creation (owned by Identity).
- Which integration events Identity publishes and consumes (for example user registered, or user disabled).

**Constraint:** Identity must not become the owner of tenant or merchant lifecycle management.

## 7.4 Token strategy

Decide and justify:

- **Lifetimes:** access token and refresh token lifetimes, and absolute versus sliding expiry.
- **Rotation and theft:**
  - refresh token rotation, and reuse detection (revoke the token family on reuse)
  - token revocation
- **Logout and sessions:**
  - logout, and logout from all sessions
  - concurrent session limits, if any
  - session invalidation on password change, account disablement or role change
- **Keys:**
  - signing algorithm
  - signing key storage, rotation and publication (JWKS)
- **Claims:** issuer and audience strategy (per-service audiences versus a shared one), and the required and forbidden JWT claims.
- **Tenant context:** which tenant information (if any) a token carries, given that a claim is never sufficient authorization proof ([ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)).
- **Token size and staleness:** what stale-claim window is acceptable, given that claims can go stale.

### 7.4.1 Token validation architecture

**Intended direction (to be confirmed in Phase 2, not a provider-specific decision):** services validate access tokens **locally**. They should **not** call the Identity service on every authenticated API request just to validate a JWT. That would make Identity a synchronous dependency, and a single point of failure and latency, for the whole platform.

Evaluate and decide:

| Topic | Questions |
|---|---|
| **Local signature validation** | Which signing algorithms are allowed (asymmetric only, for example RS256, PS256 or ES256, with an explicit allow-list and `alg: none` rejected)? How is validation configured consistently in every service without a shared business layer (a technical BuildingBlock may be justified here)? |
| **Issuer and audience validation** | A single issuer URI per environment. Per-service audiences or a shared API audience. How the Gateway and services each validate `aud`. Allowed clock skew. |
| **Signing key discovery and distribution** | Does the provider publish an OpenID Connect discovery document and a JWKS endpoint? How do services fetch and cache keys (cache lifetime, refresh on an unknown `kid`, behaviour when Identity is unreachable at startup or at runtime)? Can JWKS be reached through the Gateway or only on the internal network? |
| **Signing key rotation** | Rotation frequency. Publishing the new key before signing with it, and keeping the old key published for at least the maximum access token lifetime (superseded by [ADR-019](../decisions/ADR-019-signing-and-encryption-key-rollover-and-retirement.md): refresh tokens and codes are signed too, so the old key is kept until they have expired). Emergency rotation after a key compromise. Where private keys are stored (never in the repository). |
| **Token revocation** | With local validation, an issued access token stays valid until it expires. Which trade-off is acceptable? <ul><li>short access token lifetimes plus refresh token revocation (baseline)</li><li>a `jti` denylist distributed to services for high-risk events</li><li>token introspection only for selected high-risk operations</li></ul> How do logout, logout all, password change and account disablement behave within this model? |
| **Centralized versus distributed validation** | The Gateway may validate tokens as an early rejection layer. **Every service still validates tokens itself and performs its own tenant-level and resource-level authorization** ([ADR-007](../decisions/ADR-007-yarp-api-gateway.md), [ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)). Define what the Gateway validates, what it forwards, and how a service is protected when it is called directly, not through the Gateway. |
| **Service-to-service tokens** | How internal calls obtain tokens, which audience they target, and how they are validated the same way as user tokens. |

**Output:** a token validation section in the Identity architecture proposal, and, if it affects more than Identity, a dedicated ADR.

## 7.5 Logical data model

Design the logical (not physical) model. These are **candidate concepts, not mandatory entities**:

- User
- Credential
- Session
- Refresh Token
- External Login
- Email Verification
- Password Reset

Questions:

- Which concepts the chosen provider already models.
- Which data is personal data, and its retention and deletion rules.
- How audit-relevant events are captured.

**Constraint:** no tables, migrations or EF Core models in Phase 2.

## 7.6 Authorization model

Define clearly:

- **Authentication versus authorization**, and where each happens (Identity, Gateway, owning service).
- **Platform roles** (Admin-owned) versus **merchant roles** (Tenant & Merchant-owned) versus **customer access**.
- **Tenant context:** how it is established, validated and propagated (HTTP and Kafka messages).
- **Resource-level authorization:** the owning service validates tenant and resource ownership itself. A role or tenant claim in a JWT is **never sufficient on its own**.
- **Service-to-service authorization:** what a calling service is allowed to do, and how that is expressed.
- **Policy implementation approach** in ASP.NET Core (policies, requirements and handlers), kept consistent across services without creating a shared business layer.

## 7.7 Identity API design

Candidate endpoint areas (to be evaluated, not assumed):

- registration
- login
- refresh token
- logout and logout from all sessions
- current user
- session management
- email confirmation
- password reset

Questions:

- Which of these are replaced by **standard protocol endpoints** of the selected provider (for example `/connect/token`, `/connect/authorize`, `/connect/logout`, `/.well-known/openid-configuration`), and which remain custom REST endpoints.
- How endpoints are versioned and exposed through the Gateway.
- The error contract (problem details) and how to avoid account enumeration in responses.

**Baseline:** [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md) (Phase 0.3). It covers registration, login, refresh, logout, logout all, email confirmation, password reset, current user and session management. It also defines the conventions (`/api/v1`, headers, `X-Correlation-ID`, `Idempotency-Key`, RFC 9457 problem details) and the proposed JWT claims. Endpoints marked *Provider-dependent* must be re-evaluated against the selected protocol and provider.

The result is a reviewed **API contract proposal** that updates the baseline in `docs/api/`.

## 7.8 Identity security

Evaluate and specify:

- **Credentials:** the password hashing algorithm and parameters, and password policy and breach checks.
- **Abuse protection:**
  - brute-force protection (lockout, progressive delays)
  - rate limiting, at the Gateway versus in the service
  - account enumeration resistance (registration, login, password reset)
- **Token and key handling:**
  - refresh token storage (hashed at rest)
  - signing key protection and secret management, for local development versus deployed environments
  - session invalidation guarantees
- **Data handling:**
  - audit logging (what is logged, and what is **never** logged, such as passwords and tokens)
  - sensitive data handling and personal data minimization
  - no sensitive data in integration events

## 7.9 Phase 2 deliverables

| # | Deliverable | Status |
|---|---|---|
| 1 | Identity architecture proposal | Partly done: the component boundaries are in [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md) |
| 2 | Provider comparison (7.2 criteria, verified facts, recommendation) | **Done** (ADR-015) |
| 3 | Authentication protocol decision | **Done:** OAuth 2.0 and OpenID Connect (ADR-015). Client flows and browser architecture: [ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md) and [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) |
| 4 | Token strategy, including the token validation architecture (7.4.1) | **Done:** [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) |
| 5 | Logical data model | Designed in the Phase 2.3 implementation within ADR-015 section 4, ADR-017 and ADR-018 |
| 6 | Authorization model (including tenant context propagation) | Token side done: no role or tenant claims, and the tenant is an explicit, service-validated request parameter (ADR-017). Service-side enforcement is designed per service. |
| 7 | API contract proposal (`docs/api/`) | **Done:** [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md) |
| 8 | Security considerations | **Done:** the failure and threat matrix, residual risks and required tests in [PHASE_2_2_AUTHENTICATION_FLOWS.md](PHASE_2_2_AUTHENTICATION_FLOWS.md#10-failure-and-threat-matrix). Rules in ADR-015 to ADR-018 |
| 9 | ADRs for each decision taken: at minimum the protocol, the provider and the token strategy | **Done:** ADR-015 to ADR-018 |
| 10 | Updates to [PROJECT_DECISIONS.md](PROJECT_DECISIONS.md) section 13 (resolved items move out of "deferred") | **Done** |

The design review and approval took place on 2026-11-18. Phase 2.3 implements the accepted decisions.
