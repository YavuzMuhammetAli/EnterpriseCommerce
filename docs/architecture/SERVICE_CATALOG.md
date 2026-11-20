# Service Catalog

EnterpriseCommerce is a multi-tenant, multi-vendor marketplace made up of **exactly ten business microservices** and one infrastructure component (the YARP API Gateway). Adding a business service (for example Customer, Pricing or Reporting) requires an explicit architecture review and an ADR.

Each service owns its data exclusively ([ADR-003](../decisions/ADR-003-database-per-service.md)). The data store mapping is defined in [ADR-011](../decisions/ADR-011-per-service-data-store-assignment.md). The consolidated decisions are in [PROJECT_DECISIONS.md](PROJECT_DECISIONS.md).

The responsibilities below set boundaries, not detailed designs. Entities, APIs and events are designed per service in later phases.

## Overview

| # | Service | Planned project prefix | Data store | Status |
|---|---|---|---|---|
| 1 | Identity | `EnterpriseCommerce.Identity` | SQL Server | Implemented in Phase 2.3 (slices S1 to S7) and tested locally. Not production-verified, and production gates are open ([IDENTITY_OPERATIONS.md section 7](../security/IDENTITY_OPERATIONS.md#7-verification-level-and-remaining-production-gates)). |
| 2 | Tenant & Merchant | `EnterpriseCommerce.TenantMerchant` *(provisional name)* | SQL Server | Planned |
| 3 | Catalog | `EnterpriseCommerce.Catalog` | MongoDB | Planned |
| 4 | Inventory | `EnterpriseCommerce.Inventory` | SQL Server | Planned |
| 5 | Cart | `EnterpriseCommerce.Cart` | SQL Server + Redis (acceleration only) | Planned |
| 6 | Order | `EnterpriseCommerce.Order` | SQL Server | Planned |
| 7 | Payment | `EnterpriseCommerce.Payment` | SQL Server | Planned |
| 8 | Fulfillment | `EnterpriseCommerce.Fulfillment` | SQL Server | Planned |
| 9 | Notification | `EnterpriseCommerce.Notification` | SQL Server | Planned |
| 10 | Admin | `EnterpriseCommerce.Admin` | SQL Server | Planned |
| — | API Gateway (infrastructure) | `EnterpriseCommerce.Gateway` | none | Configuration-based routing, internal-path blocking and edge rate limiting implemented (Phase 1, Phase 2.3 S5 and S6). Deployed routing configuration is deferred. |
| — | Backend-for-frontend (infrastructure, one codebase, one deployment per front end) | `EnterpriseCommerce.Bff` (`src/Bff`) | Dedicated `noeviction` Redis for server-side sessions (`redis-sessions`, profile `bff`) | Implemented in Phase 2.3 S7 ([ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md), [BFF_API_CONTRACT.md](../api/BFF_API_CONTRACT.md)); real Safari gate open |

## 1. Identity

**Responsibility:** user identity and authentication for customers, merchant employees and platform administrators.

- Owns:
  - user identity and credentials
  - authentication and sessions
  - token issuance and validation responsibilities, and the refresh token lifecycle
  - password management
  - identity-related security processes (for example email verification and password reset)
- Does not own:
  - tenant or merchant lifecycle, membership or merchant roles (Tenant & Merchant)
  - platform administrator assignments (Admin)
- Provider and protocol (decided in Phase 2.1):
  - ASP.NET Core Identity with OpenIddict, using OAuth 2.0 and OpenID Connect ([ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)).
  - ASP.NET Core Identity roles are not authoritative for platform or merchant roles.
- Token, session and protocol strategy (decided in Phase 2.2, [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md)): Identity has its own host behind the Gateway, hosts the account UI for every credential entry, and owns the per-user security version and the refresh-token redemption ledger.
- Account security tokens ([ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)): Identity creates, hashes and consumes confirmation and reset tokens, and hands security emails to Notification through a bounded in-process queue. It does not own email delivery.
- Account status: Identity owns the authentication status of an account. Admin decides on disablement and reactivation and calls Identity's internal API with a service token ([IDENTITY_API_CONTRACT.md section 2.12](../api/IDENTITY_API_CONTRACT.md#212-account-disablement-and-reactivation-internal)). Admin keeps the administrative audit trail; Identity records the security event.
- **Implemented in Phase 2.3** and tested locally against SQL Server 2022 (progress and verification notes: [PHASE_2_IDENTITY_DESIGN_BRIEF.md](PHASE_2_IDENTITY_DESIGN_BRIEF.md#phase-23-implementation-progress); operations: [IDENTITY_OPERATIONS.md](../security/IDENTITY_OPERATIONS.md)):
  - **Persistence (S1):** EF Core migrations, and the authoritative security transaction with a whole-transaction deadlock retry and post-commit cleanup.
  - **Authentication (S2):** the Identity-hosted login page with lockout, a progressive per-account delay and rate limiting. Authorization code with PKCE `S256`. A confirmed email is required.
  - **Token handling (S2):**
    - RS256 `at+jwt` access tokens with minimal claims and one audience per front end
    - client credentials for `svc-` service clients, with 5-minute access tokens by default (finding I38)
    - rotated refresh tokens without leeway, the redemption ledger and the presenter-checked reuse policy
    - the absolute session cap, and the security version checked at every grant
  - **Registration and email confirmation (S3):** hashed, single-use confirmation tokens, resend limits, and security email delivery records with a bounded queue, a worker and status-only reconciliation.
  - **Session revocation and password management (S4):**
    - token revocation, and the POST-only end-session
    - logout-all
    - password change and password reset, each raising the security version in the authoritative transaction
  - **Routing (S5):** its own host behind the Gateway, trusted forwarded headers and host filtering.
  - **Account lifecycle (S6):** the internal disable and reactivate API for an allow-listed service client ([IDENTITY_API_CONTRACT.md section 2.12](../api/IDENTITY_API_CONTRACT.md#212-account-disablement-and-reactivation-internal)).
  - **Key rollover (S6, [ADR-019](../decisions/ADR-019-signing-and-encryption-key-rollover-and-retirement.md)):** explicit `Pending`, `Active` and `Retired` signing and encryption keys, and the automatic retirement of old keys.
  - **Retention (S6):** cleanup of the redemption ledger and of OpenIddict tokens and authorizations once they can no longer reject any token.
  - **Operations (S6):**
    - Data Protection keys persisted and encrypted at rest
    - liveness and readiness checks, and bounded-tag metrics
    - log redaction with a CI secret scan
- **Not implemented yet:** UserInfo, `GET /api/v1/users/me`, and the session list and revoke endpoints ([IDENTITY_API_CONTRACT.md section 2.1](../api/IDENTITY_API_CONTRACT.md#21-summary)).
- **External integrations, not complete:**
  - **Admin** does not exist. No client is allow-listed for the internal account lifecycle API outside the tests.
  - **Notification** has not implemented the security email endpoint ([IDENTITY_API_CONTRACT.md section 4a](../api/IDENTITY_API_CONTRACT.md#4a-outbound-security-email-handover-to-notification-internal)). Until it does, Identity refuses to start outside Development.
  - **Monitoring:** no metrics exporter is connected.
- **Production gates (open):**
  - a KMS or HSM, or an accepted certificate-file key-management procedure
  - a shared Data Protection volume with a multi-instance rolling restart, and rotation of the Data Protection key-encryption certificate
  - administrator MFA, and real Safari
  - the retention decision for security tokens and delivery records

  The full list is in [IDENTITY_OPERATIONS.md section 7](../security/IDENTITY_OPERATIONS.md#7-verification-level-and-remaining-production-gates).
- API contract: [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md).
- Phase 1 state (historical): a compilable four-layer skeleton with a health endpoint, OpenAPI, exception handling and structured logging.

## 2. Tenant & Merchant

**Responsibility:** the organizational structure of the marketplace.

- Owns:
  - the tenant lifecycle
  - merchant registration and onboarding, and merchant profiles
  - merchant membership, merchant employee invitations and merchant membership roles
  - subscription and usage management
- Is the authority for tenant ownership and membership ([ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)).
- Does not own user credentials or authentication (Identity), or products (Catalog).
- The tenancy model is **accepted** ([ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)): logical isolation with no database per tenant, and this service as the owner of the tenant and merchant lifecycle.
- Open decision: **naming only**. The final service and project name (`TenantMerchant` is provisional) and related naming conventions are still open.

## 3. Catalog

**Responsibility:** what can be sold, and how it is described and found.

- Owns:
  - product information and descriptions
  - categories and product attributes
  - the product publication lifecycle
  - catalog search and retrieval
- Data store: MongoDB.
- Does not own stock levels (Inventory) or order-time snapshots (Order).

## 4. Inventory

**Responsibility:** stock availability.

- Owns:
  - stock levels and stock adjustments
  - inventory reservations and reservation expiration
  - stock availability and inventory concurrency control
- **Correctness relies on SQL Server transactions, concurrency control and unique reservation rules.** Redis locks are never the source of truth.
- Reservation expiration is an explicit mechanism with a corresponding event ([ADR-013](../decisions/ADR-013-saga-based-workflow-coordination.md)).

## 5. Cart

**Responsibility:** a shopper's in-progress selection before checkout.

- Owns: shopping carts, cart items, the cart lifecycle, and cart validation and expiration.
- Data store: SQL Server is the durable store. Redis may accelerate cart access and short-lived operations, and Cart stays correct without it.
- MVP constraint: a cart checks out with items from a **single merchant**.

## 6. Order

**Responsibility:** orders, and checkout coordination.

- Owns:
  - the order lifecycle and order state transitions
  - checkout orchestration and checkout failure handling
  - order snapshots
  - cancellation workflows
- Orders keep **immutable snapshots** taken at checkout: product name, SKU, unit price, currency and quantity. Catalog price changes never modify historical orders.
- `OrderConfirmed` is published only after checkout completes successfully. Checkout compensation and the cancellation of confirmed orders are separate workflows ([ADR-013](../decisions/ADR-013-saga-based-workflow-coordination.md)).

## 7. Payment

**Responsibility:** payments for orders.

- Owns:
  - payment authorization (simulated)
  - payment status and payment transaction records
  - payment failure handling
  - void and refund workflows
- **Simulated initially.** Raw card data is **never collected, stored or processed**.

## 8. Fulfillment

**Responsibility:** getting confirmed orders to customers.

- Owns:
  - shipment preparation and the shipment lifecycle
  - fulfillment status and tracking information
  - delivery updates
- Does not own the order lifecycle (Order). It publishes fulfillment progress for others to consume.

## 9. Notification

**Responsibility:** delivering messages to users.

- Owns:
  - email notification workflows
  - order-related and account-related notifications
  - notification delivery status
  - retry and failure handling
- Mostly event-driven. It must not become a place where other services' business rules live.
- Security emails (confirmation and password reset links) arrive through an authenticated internal endpoint called by Identity with a service token, never through Kafka. Notification never persists the link or the rendered body of a security email ([ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)). The endpoint contract Identity calls is defined in [IDENTITY_API_CONTRACT.md section 4a](../api/IDENTITY_API_CONTRACT.md#4a-outbound-security-email-handover-to-notification-internal); Notification has not implemented it yet.

## 10. Admin

**Responsibility:** platform-level governance.

- Owns:
  - platform administrator roles and assignments
  - platform policies
  - administrative operations
  - audit records
  - platform-level monitoring and management operations
- Does **not** own user credentials or authentication sessions (Identity).
- Does not read other services' databases. It works through their APIs and events.

## API Gateway (infrastructure)

`EnterpriseCommerce.Gateway` is a YARP reverse proxy and the single external entry point ([ADR-007](../decisions/ADR-007-yarp-api-gateway.md)). It routes requests and applies edge concerns only. It contains no business logic, and its authorization never replaces service-level authorization. See [SOLUTION_STRUCTURE.md](SOLUTION_STRUCTURE.md#gateway-responsibility).

It routes Identity and each backend-for-frontend by host name, and it is the only proxy whose forwarded headers Identity trusts ([ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md), [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md)).

**Implemented** (`src/Gateway/EnterpriseCommerce.Gateway`):

- **Routing from configuration only.** YARP routes and clusters come from the `ReverseProxy` section. The development route set (`appsettings.Development.json`) contains:
  - Identity by host name (`id.localhost:7000`)
  - each BFF by host name (`storefront`, `merchant` and `admin.localhost:7000`), with `X-Forwarded` set
  - a `/identity` path-prefix route to Identity, for development diagnostics only (ADR-017 section 10)
- **Internal paths are never forwarded.** `/internal/**` answers `404` on every host, case-insensitively (IDENTITY_API_CONTRACT.md section 2.12).
- **Edge rate limiting** for the Identity account pages: the `identity-account` policy, a fixed window per client IP (default 30 requests per minute), answering `429` with `Retry-After`. Counters are kept in memory per instance. Identity applies its own limits as well.
- **Its own `/health` liveness,** never proxied. Problem details for errors, and Serilog request logging with YARP logs at Warning.
- **No token validation and no authorization.** Services authorize every request themselves (ADR-007, ADR-017 section 4).

**Verification:**

- **Tested:** internal-path blocking and the forwarding of other paths (`EdgeRoutingTests`).
- **Tested on the Identity side only:** Identity's trust of the Gateway's forwarded headers is tested against a stand-in proxy (`TrustedProxyTests`), not through the real Gateway.
- **Not covered by an automated test:** the rate limiter.
- **Not verified:** the full local flow through the Gateway (finding I29).

**Deferred** (deployment concerns, not implemented):

- production routes and clusters: `appsettings.json` defines none
- public host names and TLS certificates per environment (locally, finding I29)
- the Gateway's network address configured as the only trusted proxy of Identity and the BFFs
- a rate-limit store shared across Gateway instances
- optional early token validation, which ADR-017 section 4 allows
