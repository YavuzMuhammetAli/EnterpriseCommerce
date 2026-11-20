# ADR-015: Identity Provider: ASP.NET Core Identity with OpenIddict

- **Status:** Accepted. Amended by [ADR-017](ADR-017-token-session-and-protocol-strategy.md): the scope of R1 covers every credential entry (Identity-hosted pages only), and the reuse, leeway and revocation semantics of R4 are replaced by ADR-017 sections 5 to 8. The recommendations R2, R3, R5 and R7 and open questions O1 to O9, O11 and O12 are resolved by [ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md), ADR-017 and [ADR-018](ADR-018-account-security-tokens-and-security-email-delivery.md). The retirement period in R7 ("the maximum access token lifetime plus allowed clock skew") is superseded by [ADR-019](ADR-019-signing-and-encryption-key-rollover-and-retirement.md): refresh tokens and authorization codes are signed with the signing key too, so a retired key is kept until they have expired.
- **Date:** 2026-11-17
- **Phase:** 2.1 (Identity provider selection)
- **Resolves:** "Identity protocol and provider" in [PROJECT_DECISIONS.md section 13](../architecture/PROJECT_DECISIONS.md#13-explicitly-deferred-decisions). It supersedes no earlier ADR.

## Context

The Identity service must authenticate three kinds of users: customers, merchant users and platform administrators. It also issues the tokens that the other nine services validate. [PHASE_2_IDENTITY_DESIGN_BRIEF.md](../architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md) sections 7.1 and 7.2 list the protocol options, the candidate providers and the comparison criteria.

The decision is bounded by these accepted constraints:

- **Data:** Identity owns its own SQL Server database, accessed through EF Core ([ADR-003](ADR-003-database-per-service.md), [ADR-011](ADR-011-per-service-data-store-assignment.md)).
- **Layers:** Domain and Application never reference infrastructure or web frameworks ([ADR-002](ADR-002-clean-architecture-per-service.md)).
- **Gateway:** the Gateway may validate tokens, but authentication flows and token issuance belong to Identity. Gateway authorization never replaces service authorization ([ADR-007](ADR-007-yarp-api-gateway.md)).
- **Tenancy:** a tenant ID or token claim is never authorization proof. Every service authorizes tenant-level and resource-level access itself ([ADR-014](ADR-014-logical-tenant-isolation.md)).
- **Ownership:**
  - platform administrator roles and assignments belong to Admin
  - tenants, merchants, memberships and merchant roles belong to Tenant & Merchant
  - Identity owns neither ([PROJECT_DECISIONS.md section 5](../architecture/PROJECT_DECISIONS.md#5-business-microservices))
- **Validation:** the intended direction is that services validate JWT access tokens locally and don't call Identity on every request ([PROJECT_DECISIONS.md section 10](../architecture/PROJECT_DECISIONS.md#10-security)).
- **No new runtime or infrastructure component** without an ADR. The local Docker VM has about 3.8 GB of memory.

The project owner selected **ASP.NET Core Identity together with OpenIddict** as the architectural direction. This ADR records that decision, validates the integration design, and sets the responsibility boundaries. It separates what is decided now from what is recommended and from what remains open for Phase 2.2.

### Facts verified at decision time

These were checked against official sources on 2026-11-17. Re-verify them when the implementation phase starts.

| Fact | Source |
|---|---|
| OpenIddict is a free, open-source OAuth 2.0 / OpenID Connect framework. It is licensed under Apache-2.0. | [OpenIddict introduction](https://documentation.openiddict.com/introduction.html), [openiddict-core repository](https://github.com/openiddict/openiddict-core) |
| The current major version is 7.x. It targets ASP.NET Core 10.0 / .NET 10.0, alongside .NET 8 and 9. Migrating from 6.x to 7.0 required a database schema change. | [Migrate to OpenIddict 7.0](https://documentation.openiddict.com/guides/migration/60-to-70.html) |
| The server encrypts all the token types it supports by default. Access token encryption can be turned off with `DisableAccessTokenEncryption()`. Encryption of authorization codes, refresh tokens and device codes **cannot** be turned off. | [Token formats](https://documentation.openiddict.com/configuration/token-formats.html) |
| By default, OpenIddict creates a database entry for each token it issues. The entry holds metadata, not the token payload, except for authorization codes and device and user codes. Reference access and refresh tokens are optional. By default the validation stack does **not** check a token entry's status when an API request arrives. `EnableTokenEntryValidation()` turns that check on. | [Token storage](https://documentation.openiddict.com/configuration/token-storage.html) |
| Development certificates and ephemeral keys are for development only. For production, OpenIddict recommends two RSA certificates, one for signing and one for encryption, distinct from the HTTPS certificate. When several keys are registered, it prefers valid X.509 certificates with the furthest expiration date and ignores certificates that are not yet valid. | [Encryption and signing credentials](https://documentation.openiddict.com/configuration/encryption-and-signing-credentials.html) |
| PKCE can be required globally (`RequireProofKeyForCodeExchange()`) or per client. | [Proof Key for Code Exchange](https://documentation.openiddict.com/configuration/proof-key-for-code-exchange.html) |
| The client credentials flow is supported. The server exposes revocation and introspection endpoints. | [Creating your own server instance](https://documentation.openiddict.com/guides/getting-started/creating-your-own-server-instance), [Mutual TLS authentication](https://documentation.openiddict.com/configuration/mutual-tls-authentication.html) |
| Certificate-bound access tokens, and certificate-bound refresh tokens for public clients, are supported (OAuth 2.0 mutual TLS, RFC 8705). | [Mutual TLS authentication](https://documentation.openiddict.com/configuration/mutual-tls-authentication.html) |

**Not verified yet (verify in Phase 2.2 before relying on them):**

- the default refresh token rotation behaviour of the current version
- the exact refresh token reuse detection and reuse leeway behaviour, and their configuration options
- whether keys that are not yet valid are published in the JWKS document (this matters for rotation)
- DPoP support
- OpenID certification status

## Decision

The decision has three tiers:

- **Accepted:** decided by this ADR.
- **Recommended:** the proposed direction, confirmed or changed in Phase 2.2 or a later design step.
- **Open:** needs a separate decision.

### 1. Accepted decisions

**A1. Architecture.**

- The Identity service is the **OAuth 2.0 / OpenID Connect authorization server** of the platform.
- It is built with **OpenIddict** (server stack, with its Entity Framework Core stores) and **ASP.NET Core Identity** (user and credential store).
- Both run inside the Identity service and persist to the Identity service's own SQL Server database through EF Core.
- This also settles the protocol family: **OAuth 2.0 and OpenID Connect**. Which flows each client type uses is decided in Phase 2.2, starting from the recommendations in section 2.
- No external identity product, no managed identity platform and no new runtime is introduced.

**A2. Responsibility boundaries.**

| ASP.NET Core Identity | OpenIddict | EnterpriseCommerce (application code) |
|---|---|---|
| User account lifecycle | OAuth 2.0 and OpenID Connect protocol implementation | Marketplace authorization policies |
| Password hashing and validation | Authorization and token endpoints | Platform roles and permissions: owned by **Admin** |
| Account lockout | Access token issuance | Tenant and merchant membership authorization: owned by **Tenant & Merchant**, enforced by each owning service |
| Email confirmation | Refresh token issuance and redemption | Resource-level authorization: each owning service |
| Password reset | Token revocation | Service-level tenant isolation ([ADR-014](ADR-014-logical-tenant-isolation.md)) |
| User management (see A3 for roles) | Client registration and configuration | Business-specific authentication workflows in Identity, for example registration rules, the email confirmation policy, and the session and logout-all semantics |
| Security-related account metadata (security stamp, lockout state, confirmation flags, external logins) | Protocol-related persistence (applications, authorizations, scopes, tokens) | Which claims Identity places in tokens, and how they are sourced |

**Marketplace business authorization is never delegated to OpenIddict.** OpenIddict authenticates clients and users, and it issues and revokes tokens. It does not decide whether a user may act on a tenant, merchant, product, order or any other business resource.

**A3. ASP.NET Core Identity roles are not authoritative.**

- The ASP.NET Core Identity role store (`AspNetRoles`, `AspNetUserRoles`) is **not the source of truth** for platform roles or merchant roles.
- Platform administrator roles and assignments stay with **Admin**. Merchant membership roles stay with **Tenant & Merchant** ([PROJECT_DECISIONS.md section 5](../architecture/PROJECT_DECISIONS.md#5-business-microservices)).
- Whether Identity keeps a read-only projection of those roles, built from Admin and Tenant & Merchant integration events and used only to populate token claims, is an **open question** for the token strategy (section 3).

**A4. Rejected grants.**

- The **Resource Owner Password Credentials** grant is not enabled for any client.
- The **implicit** flow is not enabled for any client, including Angular applications.

Both are discouraged by current OAuth security guidance (RFC 9700, OAuth 2.0 Security Best Current Practice). They would expose credentials or tokens to clients unnecessarily.

**A5. Claims are not authorization proof, and the Gateway does not replace services.** This restates [ADR-007](ADR-007-yarp-api-gateway.md) and [ADR-014](ADR-014-logical-tenant-isolation.md):

- Role and tenant claims in a JWT are never sufficient authorization proof.
- The Gateway may validate tokens as an early rejection layer.
- **Every service validates every token itself**, and performs its own tenant-level and resource-level authorization, including when it is called directly rather than through the Gateway.

**A6. Layer placement.** This applies the existing Clean Architecture rule ([ADR-002](ADR-002-clean-architecture-per-service.md)) to the new frameworks:

- ASP.NET Core Identity and OpenIddict types live only in **Identity.Infrastructure**: stores, the EF Core `DbContext`, and entity configuration. Identity.Api holds the protocol endpoints, authentication handlers and DI composition.
- **Identity.Domain and Identity.Application never reference** `Microsoft.AspNetCore.Identity*`, `Microsoft.Extensions.Identity.*` or `OpenIddict.*`. The Application layer reaches these capabilities only through its own interfaces.
- The current architecture tests forbid `Microsoft.AspNetCore*` but not `Microsoft.Extensions.Identity.*` or `OpenIddict.*`. They must be extended **before** the first Identity implementation phase adds these packages. Until then this rule is enforced by review.

**A7. Separation between Identity users and OpenIddict entities.**

- **User data:** ASP.NET Core Identity entities hold user and credential data only. They hold no protocol state such as authorizations, tokens or client registrations.
- **Protocol data:** OpenIddict entities (applications, authorizations, scopes and tokens) hold protocol state only. They refer to a user only through the **subject** (`sub`) value. There is no foreign key from OpenIddict tables into the user tables, and no business data is stored in OpenIddict entities.
- **The `sub` value:** a stable, opaque, non-reassignable user identifier, **never an email address** ([IDENTITY_API_CONTRACT.md section 3](../api/IDENTITY_API_CONTRACT.md#3-jwt-access-token-claims-baseline)).

**A8. Data and secret handling.**

- **Passwords:** raw passwords are never stored or logged. They are hashed by the ASP.NET Core Identity password hasher, whose algorithm parameters are confirmed in the security design.
- **Payment data:** the Identity database never stores payment information of any kind.
- **Keys:** signing and encryption keys and certificates are **never committed** to the repository.
- **Development keys:** development certificates and ephemeral keys may be used only in the Development environment. Every other environment uses keys loaded from a protected store.
- **Logs:** tokens, authorization codes, client secrets and passwords are never logged.

### 2. Recommendations (to be confirmed in Phase 2.2 or later)

**R1. Browser clients use Authorization Code with PKCE.**

- Every interactive client uses the **authorization code flow with PKCE (`S256`)**.
- PKCE is required for **all** clients, confidential ones included.
- The three Angular front ends (customer storefront, merchant portal and admin console) are registered as **separate OAuth clients**, with their own redirect URIs, scopes and audiences.
- Interactive login is rendered by the Identity service. Credentials are never entered into a client application.

Flow evaluation:

| Flow | Use | Assessment |
|---|---|---|
| Authorization code + PKCE | Angular front ends, Swagger UI in Development | **Recommended.** Current best practice for browser and native clients. |
| Implicit | — | **Rejected (A4).** Tokens are returned in the URL. |
| Resource owner password credentials | — | **Rejected (A4).** The client handles user passwords. |
| Client credentials | Service-to-service calls (R5) | **Recommended** for workload authentication. |
| Refresh token | Session continuation (R4) | **Recommended**, with rotation and reuse detection |
| Device authorization | — | Out of scope (no input-constrained devices planned) |

**R2. Evaluate a Backend-for-Frontend (BFF) for browser clients.**

Authorization code + PKCE is the flow in both options. They differ in **where the tokens live**:

| | Public SPA client (tokens in the browser) | Backend-for-Frontend (tokens on the server) |
|---|---|---|
| Client type | Public (no client secret) | Confidential (a server-side client secret or key) |
| Where tokens live | Browser memory or storage, reachable by injected script | Server side only; the browser holds an `HttpOnly`, `Secure`, `SameSite` session cookie |
| Exposure to XSS | A script injection can steal the access token and the refresh token | A script injection can make requests during the session, but can't take tokens out |
| Refresh tokens | Must be rotated with reuse detection, and preferably sender-constrained (RFC 9700) | Kept server side, with less exposure |
| CSRF | Not applicable to bearer tokens | Must be mitigated (`SameSite` cookies plus an anti-forgery check) |
| Components | No additional component | An additional deployable per front end, or a shared one. **This needs its own ADR**, because it is a new component |
| Fit with the Gateway | The Gateway forwards bearer tokens | The BFF calls the Gateway with access tokens. The cookie never reaches the services |

**Recommendation:** prefer a BFF for the **merchant portal and the admin console**, because they carry high privileges. Decide the customer storefront in Phase 2.2. A public SPA client is acceptable there only with short-lived access tokens, rotated refresh tokens and a strict Content Security Policy. This choice drives refresh token delivery ([IDENTITY_API_CONTRACT.md section 4](../api/IDENTITY_API_CONTRACT.md#4-open-items-for-phase-2-review), item 3).

**R3. Use signed, unencrypted JWT access tokens.**

- Issue access tokens as **signed JWTs (JWS)** by calling `DisableAccessTokenEncryption()`.
- Every service then validates them **locally**: signature against the published JWKS, `iss`, `aud`, `exp`, `nbf` and the algorithm allow-list. This matches the intended direction in [PROJECT_DECISIONS.md section 10](../architecture/PROJECT_DECISIONS.md#10-security).
- Access tokens carry no secrets and no unnecessary personal data, so encrypting them adds little protection.
- **Alternative:** keep encrypted access tokens (JWE) and use the OpenIddict validation handler in every service. That spreads the decryption key to every resource server and couples all services to an OpenIddict package. Not recommended.

**R4. Refresh tokens: rotation, reuse detection, expiry and revocation.**

- **Rotation:** every redemption issues a new refresh token and invalidates the previous one.
- **Reuse detection:**
  - Presenting an already-redeemed refresh token revokes the **whole token family**, that is, the OpenIddict authorization and all tokens issued under it.
  - Any reuse leeway for concurrent requests is kept short and explicit, and its security trade-off is documented.
  - The OpenIddict defaults and option names for rotation and reuse must be verified first (see "Not verified yet").
- **Expiration:** short-lived access tokens, and refresh tokens with a sliding lifetime **capped by an absolute session lifetime**. The values are decided in the Phase 2.2 token strategy.
- **Revocation:**
  - Logout revokes the session's authorization.
  - Logout-all, password change, password reset and account disablement revoke every authorization of the user. The ASP.NET Core Identity security stamp is updated, which ends the interactive login session too.
- **Storage:** refresh tokens are encrypted by OpenIddict (this can't be disabled), and their entries are tracked in the database. Raw refresh token values are never logged.
- **Access tokens issued before revocation** stay valid until they expire. That is the trade-off of local validation ([PHASE_2_IDENTITY_DESIGN_BRIEF.md section 7.4.1](../architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md#741-token-validation-architecture)). Short access token lifetimes are the baseline mitigation. A `jti` denylist or introspection for high-risk operations stays optional.
- **Sender-constraining:** certificate-bound refresh tokens (mTLS) are available. DPoP support is unverified. Sender-constraining is evaluated together with R2.

**R5. Service-to-service authentication uses client credentials.**

- Each calling service is a **confidential client** that obtains tokens with the **client credentials grant**.
- Its token targets the **audience of the called service**. Scopes express the operations the caller may invoke.
- The called service validates these tokens **exactly like user tokens** (R3) and authorizes the calling client explicitly.
- When an internal call acts for a tenant, the tenant context is passed explicitly and **authorized again by the called service**. A service token never grants cross-tenant access by itself.
- Client secrets or keys come from configuration and secret stores, never from the repository.
- mTLS client authentication and certificate-bound tokens are evaluated for deployed environments.
- Kafka message authentication is out of scope for this ADR.

**R6. Customers, merchant users and platform administrators share one identity infrastructure.**

- **One user store:** there is one user store and one authorization server. A single account may be a customer, a merchant employee in one or more merchants, and a platform administrator, all at once.
- **What Identity knows:** Identity knows **who** the user is and **how** they authenticated (`sub`, `amr`, authentication time). It doesn't own **what** they may do in the marketplace:
  - platform roles: Admin
  - memberships and merchant roles: Tenant & Merchant
  - resources: the owning service
- **Separation between front ends:** the separate clients (R1), with distinct scopes and audiences, keep the front ends apart. A token issued to the storefront client can't call admin-only APIs, because those APIs require their own audience and scope **and** still check Admin-owned authorization.
- **Stronger authentication:** the admin console may require a stronger authentication method (`amr`, for example MFA). MFA enrolment is designed later.

**R7. Signing keys, rotation, JWKS publication and validation (operations).**

- **Certificates:** use **separate signing and encryption certificates** (asymmetric, distinct from TLS certificates), as OpenIddict recommends.
- **Storage:**
  - Development uses OpenIddict development certificates, which are never committed.
  - Deployed environments load keys from a protected certificate or secret store. The concrete store is decided per environment.
- **Rotation:**
  - Register and publish the next signing key **before** it becomes the active signing key.
  - Keep the retired key published for at least the maximum access token lifetime plus allowed clock skew.
  - Document an **emergency rotation** procedure for key compromise. After an emergency rotation, every token signed with the compromised key is rejected.
  - Verify how OpenIddict publishes keys that are not yet valid before relying on pre-publication.
- **Publication:** the discovery document (`/.well-known/openid-configuration`) and the JWKS endpoint are published by OpenIddict. Only public keys are published.
- **Validation in services:**
  - Services cache the JWKS, refresh it on an unknown `kid` (with rate limiting), and keep the cached keys when Identity is unreachable.
  - They accept only an explicit asymmetric algorithm allow-list and reject `alg: none` and symmetric algorithms.
  - They validate `iss` and `aud`, and use a small, explicit clock skew.
- **Issuer:** a **single, stable issuer URI per environment**. Exposing the protocol endpoints through the Gateway's `/identity` prefix must not produce a mismatch between the issuer in tokens, the discovery document and the URLs that clients and services use.
- **Data Protection:** ASP.NET Core Data Protection keys protect cookies and ASP.NET Core Identity tokens. They need persistent, shared storage that is separate from OpenIddict signing keys.

### 3. Open questions (decided in Phase 2.2 or a later design step)

| # | Question |
|---|---|
| O1 | BFF versus public SPA client for each front end (R2), and therefore refresh token delivery |
| O2 | Access token, refresh token and absolute session lifetimes. Refresh token reuse leeway |
| O3 | The signing algorithm allow-list (RS256, PS256 or ES256) |
| O4 | The resource-server validation library: the standard ASP.NET Core JWT bearer handler versus the OpenIddict validation handler, and whether a technical BuildingBlock is justified for consistent configuration |
| O5 | Platform roles and merchant memberships in tokens: a read-only projection in Identity built from events (A3), lookups at authorization time, or no role claims at all. Also the acceptable staleness window |
| O6 | The shape of the `tenant_context` claim, if any ([ADR-014](ADR-014-logical-tenant-isolation.md)) |
| O7 | Sender-constrained tokens: DPoP versus mTLS versus none, per client type |
| O8 | Where the ASP.NET Core Data Protection key ring and the deployed-environment signing keys are stored |
| O9 | Email confirmation and password reset token semantics. The baseline contract expects single-use tokens stored hashed ([IDENTITY_API_CONTRACT.md section 2.7](../api/IDENTITY_API_CONTRACT.md#27-email-confirmation)). ASP.NET Core Identity's default tokens are Data Protection-based and security-stamp-bound, and are not stored. Either custom token providers or a revised contract is needed |
| O10 | Whether Identity.Domain has its own user model mapped by Infrastructure to the ASP.NET Core Identity user entity, or Identity's domain stays minimal and delegates the account lifecycle to ASP.NET Core Identity through Application interfaces |
| O11 | The issuer URI, and how the protocol endpoints, discovery and JWKS are routed through or around the Gateway |
| O12 | Mapping the provider-dependent endpoints of [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md) (login, refresh, logout) to the standard protocol endpoints (`/connect/authorize`, `/connect/token`, `/connect/endsession`, `/connect/revoke`, UserInfo), and which custom REST endpoints remain |
| O13 | The Identity integration events (for example user registered, user disabled), designed with the first event-flow phase ([ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md)) |

### 4. Data persisted in the Identity SQL Server database

This is a logical view, not a schema. The detailed data model is a Phase 2 deliverable.

| Owner | Data | Notes |
|---|---|---|
| ASP.NET Core Identity | Users: identifier, normalized email and user name, password hash, security stamp, concurrency stamp, email confirmation, lockout state and failed access count, two-factor flags | Personal data. Retention and deletion rules are designed in Phase 2 |
| ASP.NET Core Identity | External logins, user claims, user tokens (for example authenticator keys) | Only the features that are enabled. External logins are outside the MVP baseline |
| ASP.NET Core Identity | Roles and user roles | **Not authoritative** for platform or merchant roles (A3) |
| OpenIddict | Applications: client registrations, redirect URIs, permissions, requirements such as PKCE, hashed client secrets | Clients are configuration, not user data |
| OpenIddict | Authorizations: the user–client grant, which acts as the refresh token family and session anchor | Referenced by subject only (A7) |
| OpenIddict | Scopes | Map to API audiences |
| OpenIddict | Tokens: one entry per issued token with metadata and status. Payloads are not stored by default | Used for revocation, reuse detection and session listing |
| Application (candidate) | ASP.NET Core Data Protection keys | Storage location is open (O8) |

**Never stored:** raw passwords, payment information of any kind, signing private keys (they are kept in a key store, not the database), and full access token payloads (unless reference tokens are chosen later).

## Consequences

Positive:

- **Standards:** OAuth 2.0 and OpenID Connect, with a discovery document and JWKS. Services, the Gateway and any future client can use standard libraries.
- **.NET-native:** it runs inside the existing Identity service and uses EF Core with SQL Server ([ADR-011](ADR-011-per-service-data-store-assignment.md)). It adds no new runtime or infrastructure container, which fits the local memory limits.
- **Licensing:** Apache-2.0, with no licence cost or revenue threshold for a public portfolio repository.
- **Control:** full control over claims, tenant context, client registration and authentication workflows. That is needed for the marketplace's ownership boundaries.
- **Mature user store:** ASP.NET Core Identity provides proven password hashing, lockout, security stamps and token providers.

Negative:

- **Assembly:** OpenIddict is a framework, not a turnkey product. Login UI, consent, client provisioning, key handling and hardening are the project's responsibility, and protocol expertise is needed.
- **Maintenance:** OpenIddict is maintained mainly by one lead maintainer, so the bus factor is low. Track releases and security advisories, and keep the protocol layer isolated in Infrastructure and Api so that replacing it stays possible.
- **Upgrades:** major-version upgrades can require database schema migrations (as 7.0 did).
- **Operations:** key management, rotation and JWKS availability become operational responsibilities of the project.
- **Revocation lag:** local validation of self-contained access tokens means that revocation takes effect only when the access token expires, unless extra mechanisms are added (R4).
- **Framework tables:** two frameworks share one database, so the Identity schema contains framework-defined tables. Their naming and migrations must be managed deliberately.

## Alternatives considered

- **ASP.NET Core Identity alone:**
  - It provides users and credentials, but it is not an OAuth 2.0 / OpenID Connect server.
  - Its built-in API endpoints issue bearer tokens that are not JWTs (Data Protection-based), which other services can't validate with standard libraries.
  - It has no discovery, no JWKS and no client registration.
  - Rejected as a complete solution. It remains part of the selected design as the user store.
- **Duende IdentityServer:**
  - A mature, OpenID-certified framework.
  - It requires a **commercial licence**, with a Community Edition limited to qualifying organizations. Those licensing terms are a long-term dependency for a public portfolio project.
  - Not selected.
- **Keycloak or another standalone identity server:**
  - A full identity and access management product.
  - It adds a non-.NET runtime and a new infrastructure container, which is a technology change, needs more memory than the local environment comfortably provides, and moves user data out of the Identity service's own model.
  - Not selected.
- **Managed identity platform (for example Microsoft Entra External ID or Auth0):**
  - It moves credentials and authentication out of the platform, which changes Identity's ownership scope.
  - It adds a cloud dependency and cost, and prevents fully offline local development.
  - Not selected.
- **Custom token issuance (hand-written JWT endpoints):**
  - It re-implements security-critical protocol behaviour (authorization codes, PKCE, refresh token handling and revocation) without a standard implementation.
  - Rejected.
