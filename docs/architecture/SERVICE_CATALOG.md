# Service Catalog

EnterpriseCommerce is a multi-tenant, multi-vendor marketplace made up of **exactly ten business microservices** and one infrastructure component (the YARP API Gateway). Adding a business service (for example Customer, Pricing or Reporting) requires an explicit architecture review and an ADR.

Each service owns its data exclusively ([ADR-003](../decisions/ADR-003-database-per-service.md)). The data store mapping is defined in [ADR-011](../decisions/ADR-011-per-service-data-store-assignment.md). The consolidated decisions are in [PROJECT_DECISIONS.md](PROJECT_DECISIONS.md).

The responsibilities below set boundaries, not detailed designs. Entities, APIs and events are designed per service in later phases.

## Overview

| # | Service | Planned project prefix | Data store | Status |
|---|---|---|---|---|
| 1 | Identity | `EnterpriseCommerce.Identity` | SQL Server | Implemented in Phase 2.3 (slices S1 to S7) and tested locally. Not production-verified, and production gates are open ([IDENTITY_OPERATIONS.md section 7](../security/IDENTITY_OPERATIONS.md#7-verification-level-and-remaining-production-gates)). |
| 2 | Tenant & Merchant | `EnterpriseCommerce.TenantMerchant` ([ADR-020](../decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md)) | SQL Server | Phase 3 slices S1 (foundation and tenant provisioning), S2 (membership management) and S3 (self-service onboarding) implemented and tested locally. S4 (the first integration event, `TenantCreated`, through a transactional outbox and a Kafka relay, [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md)) implemented and tested locally and in CI. Not production-verified ([PHASE_3_TENANT_MERCHANT.md](PHASE_3_TENANT_MERCHANT.md)). |
| 3 | Catalog | `EnterpriseCommerce.Catalog` | MongoDB | Implemented in Phase 4 ([ADR-024](../decisions/ADR-024-catalog-service-design.md), [ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md), [PHASE_4_CATALOG.md](PHASE_4_CATALOG.md)) and tested locally against MongoDB 8.0. Not production-verified. No integration events yet. |
| 4 | Inventory | `EnterpriseCommerce.Inventory` | SQL Server | Implemented in Phase 5 ([ADR-026](../decisions/ADR-026-inventory-service-design.md), [ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md), [ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md), [PHASE_5_INVENTORY.md](PHASE_5_INVENTORY.md)) and tested locally against SQL Server 2022 and Kafka. Not production-verified. |
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
- Email verification for invitations ([ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md)): Identity answers Tenant & Merchant's service client whether an address is the verified email of an active account ([IDENTITY_API_CONTRACT.md section 2.13](../api/IDENTITY_API_CONTRACT.md#213-account-email-verification-internal)). It returns a verdict, never the account's address.
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
- **Name and cardinality ([ADR-020](../decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md)):** `TenantMerchant`. A tenant is one merchant organization, and memberships and merchant roles are tenant-scoped.
- Merchant roles: `MerchantOwner`, `MerchantAdmin`, `MerchantStaff` ([PHASE_3_TENANT_MERCHANT.md section 3](PHASE_3_TENANT_MERCHANT.md#3-merchant-roles-permissions-and-membership-rules)). They are never put into Identity tokens.
- **Implemented in Phase 3 S1** and tested locally against SQL Server 2022 and LocalDB (API: [TENANT_MERCHANT_API_CONTRACT.md](../api/TENANT_MERCHANT_API_CONTRACT.md)):
  - **Persistence:** its own database with the EF Core migration `InitialTenantMerchantSchema`. Uniqueness, allowed values and one current membership per subject and tenant are database constraints, and every table has a `rowversion` token.
  - **Provisioning:** `POST /internal/v1/tenants` creates the tenant, its merchant profile and the initial `MerchantOwner` membership in one transaction.
    - It is idempotent per (client, `Idempotency-Key`) and race-safe.
    - Callers are allow-listed `svc-` clients only (audience `tenant-merchant-api`, scope `svc.tenant-merchant.provisioning`). The list is empty by default.
  - **Member API:** `GET /api/v1/tenants/{id}` and `GET /api/v1/tenants/{id}/memberships` for `ec-merchant` users.
    - Every request is authorized from the service's own membership data.
    - Cross-tenant access answers `404`, and an insufficient role `403`.
  - **Host:** local JWT bearer validation (ADR-017 section 4), a fail-closed fallback policy, `X-Correlation-ID`, problem details, and health checks.
- **Implemented in Phase 3 S2** ([PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md](PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md), [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md)):
  - **Invitations:** a single-use token stored only as a hash and returned once to the inviter; acceptance requires Identity's verified-email verdict, and the subject is always the token's `sub`.
  - **Membership administration:** suspend, reactivate, remove and change roles under an explicit role matrix. No role change or invitation grants `MerchantOwner`.
  - **The last-active-owner guarantee under concurrency:** a per-tenant write lock, plus a trigger backstop.
  - **An append-only audit trail**, written atomically with every change.
  - **The caller's tenant list.**
- **Implemented in Phase 3 S3** ([PHASE_3_S3_SELF_SERVICE_ONBOARDING.md](PHASE_3_S3_SELF_SERVICE_ONBOARDING.md), [ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md)):
  - **Self-service onboarding:** `POST /api/v1/tenants` creates an active tenant, its merchant profile and the caller's `MerchantOwner` membership in one transaction. An owner is created only together with its new tenant, by provisioning or by this endpoint.
  - **Per-user limit and idempotency:** one tenant per user by default (configurable, 1 to 10), and keys scoped per user. Both hold under concurrency through a per-user application lock.
- **Implemented in Phase 3 S4** ([PHASE_3_S4_EVENT_FLOW.md](PHASE_3_S4_EVENT_FLOW.md), [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md)):
  - **The first integration event:** `TenantCreated` v1 on `tenant-merchant.events`, written by both creation paths in the creation's own transaction (transactional outbox).
  - **The outbox relay:** one leader per service publishes committed rows to Kafka in commit order, at least once, with backoff, a halted state and `/health/outbox` ([TENANT_MERCHANT_EVENT_RELAY.md](../operations/TENANT_MERCHANT_EVENT_RELAY.md)).
- **Implemented in Phase 4, for Catalog** ([ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md)): the internal membership verification `POST /internal/v1/tenants/{tenantId}/membership-verifications` ([TENANT_MERCHANT_API_CONTRACT.md section 3.3](../api/TENANT_MERCHANT_API_CONTRACT.md#33-verify-a-membership-internal)). It answers an allow-listed service client (scope `svc.tenant-merchant.membership-verification`, separate allow-list) whether a subject is an active member of an active tenant, and its role. Its JWT bearer setup now comes from `EnterpriseCommerce.BuildingBlocks.Authentication`. Since Phase 5, Inventory (`svc-inventory`) is a second allow-listed caller, and the outbox and relay code is shared through `EnterpriseCommerce.BuildingBlocks.Messaging` with unchanged behaviour ([ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md)).
- **Not implemented yet:**
  - consumers of its events, and membership events (deferred to the slice of their first consumer, ADR-023)
  - invitation email delivery (Notification), and the merchant-portal front end
  - merchant applications and review (with Admin)
  - tenant suspension and closure
  - subscriptions and usage
- **External integrations, not complete:** Admin, the intended provisioning caller, does not exist. No client is allow-listed outside the tests.

## 3. Catalog

**Responsibility:** what can be sold, and how it is described and found.

- Owns:
  - product information and descriptions
  - categories and product attributes
  - the product publication lifecycle
  - catalog search and retrieval
  - product variants, their SKUs and their **list prices** ([ADR-024](../decisions/ADR-024-catalog-service-design.md) section 4; Order snapshots prices at checkout)
  - tenant brands, and media references (no binary media)
- Data store: MongoDB.
- Does not own stock levels (Inventory) or order-time snapshots (Order).
- Categories are a **platform taxonomy** written by allow-listed service clients; brands are **tenant-scoped** ([ADR-024](../decisions/ADR-024-catalog-service-design.md)).
- Merchant requests are authorized per request by Tenant & Merchant's membership verification ([ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md)). Design and evidence: [PHASE_4_CATALOG.md](PHASE_4_CATALOG.md). API: [CATALOG_API_CONTRACT.md](../api/CATALOG_API_CONTRACT.md). Operations: [CATALOG_OPERATIONS.md](../operations/CATALOG_OPERATIONS.md).
- **Implemented in Phase 4:**
  - **Persistence:** its own MongoDB database. Products, categories and brands are single-document aggregates with optimistic concurrency. Fifteen named indexes are created at startup; unique indexes back slug, SKU and idempotency-key uniqueness. Writes are refused until the indexes exist.
  - **Merchant API** (`/api/v1/catalog/tenants/{tenantId}/...`):
    - products with variants, attributes, media references and the lifecycle commands
    - tenant brands
    - `If-Match` on full replacements
    - idempotent product creation
  - **Taxonomy API** (`/internal/v1/catalog/categories`): allow-listed service clients only (scope `svc.catalog.taxonomy`, empty allow-list by default).
  - **Public discovery** (`/api/v1/catalog/products`, `/api/v1/catalog/categories`): anonymous, published products and active categories only. Text, category (with descendants), brand, tenant and attribute filters, two sorts, bounded paging.
  - **Host:**
    - local JWT validation through `BuildingBlocks.Authentication`
    - fail-closed policies
    - `X-Correlation-ID` and problem details
    - a 256 KiB body limit
    - liveness independent of MongoDB, and readiness on MongoDB and the indexes
    - bounded-tag meters
- **Implemented in Phase 5, for Inventory** ([ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md)): the internal variant verification `POST /internal/v1/catalog/tenants/{tenantId}/variant-verifications` ([CATALOG_API_CONTRACT.md section 6a](../api/CATALOG_API_CONTRACT.md#6a-internal-variant-verification)). It answers an allow-listed service client (scope `svc.catalog.variant-verification`, its own allow-list) whether a variant belongs to a product of the tenant, with the product status, sellability and SKU.
- **Not implemented yet:**
  - integration events (no MongoDB outbox without a replica set; ADR-024 section 13)
  - an image upload pipeline
  - moving categories
  - discounts and price lists
  - anonymous browsing through the storefront BFF
  - visibility of tenant status (a suspended tenant's published products stay visible until tenant status reaches Catalog)

## 4. Inventory

**Responsibility:** stock availability.

- Owns:
  - stock levels and stock adjustments
  - inventory reservations and reservation expiration
  - stock availability and inventory concurrency control
- **Correctness relies on SQL Server transactions, concurrency control and unique reservation rules.** Redis locks are never the source of truth.
- Reservation expiration is an explicit mechanism with a corresponding event ([ADR-013](../decisions/ADR-013-saga-based-workflow-coordination.md)).
- Does not own products, variants or SKUs (Catalog), memberships (Tenant & Merchant), orders or checkout (Order).
- **Implemented in Phase 5** ([ADR-026](../decisions/ADR-026-inventory-service-design.md), design and evidence: [PHASE_5_INVENTORY.md](PHASE_5_INVENTORY.md), API: [INVENTORY_API_CONTRACT.md](../api/INVENTORY_API_CONTRACT.md), events: [INVENTORY_EVENTS.md](../events/INVENTORY_EVENTS.md), operations: [INVENTORY_OPERATIONS.md](../operations/INVENTORY_OPERATIONS.md)):
  - **Persistence:**
    - its own SQL Server database: one balance per (tenant, variant), whole-unit quantities, `0 ≤ Reserved ≤ OnHand` as CHECK constraints
    - an append-only, linear movement ledger
    - reservations with immutable lines and `rowversion` tokens
  - **Merchant API** (`/api/v1/inventory/tenants/{tenantId}/items`): create items of Catalog-verified variants ([ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md)), list and read balances, adjust stock with reasons, and read the movement history.
    - Authorized per request by Tenant & Merchant (ADR-025). Owner and Admin manage stock; Staff reads.
    - Idempotent creation and adjustments.
  - **Internal reservation API** (`/internal/v1/inventory/tenants/{tenantId}/reservations`): for allow-listed service clients (scope `svc.inventory.reservations`, empty by default).
    - Atomic multi-line reservations, idempotent per (client, tenant, key).
    - Commit and release, with terminal states final and lazy expiry.
    - Each client sees only its own reservations.
  - **Expiry:** a multi-instance worker on database time (`UPDLOCK, READPAST` claims), and `/health/expiry`.
  - **Events:** `StockLevelChanged` and the four reservation events on `inventory.events`, through the transactional outbox and the shared relay of `BuildingBlocks.Messaging` ([ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md)). Order is guaranteed per aggregate.
  - **Host:** local JWT validation through `BuildingBlocks.Authentication`, fail-closed policies, `X-Correlation-ID`, problem details, a 256 KiB body limit, and readiness on the database and its migrations.
- **Not implemented yet:**
  - consumers (none needed)
  - a merchant view of reservations
  - Catalog-driven archival (Catalog publishes no events)
  - a movement retention policy
  - Order, the intended reservation caller

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
  - the Tenant & Merchant member API: `/api/v1/tenants/**` on host `localhost:7000` (Phase 3 S1)
  - the Catalog API: `/api/v1/catalog/**` on host `localhost:7000` (Phase 4)
  - the Inventory merchant API: `/api/v1/inventory/**` on host `localhost:7000` (Phase 5)
- **Internal paths are never forwarded.** A path whose first segment is `internal` answers `404` on every host, by every route and in every environment.
  - The check is case-insensitive, after percent-decoding and resolving empty and dot segments.
  - It runs on the incoming path, and again on the outgoing path of every proxied request after the route's transforms, so a prefix-removing or rewriting route cannot produce an internal destination (IDENTITY_API_CONTRACT.md section 2.12; finding P3-1, resolved).
- **Edge rate limiting** for the Identity account pages: the `identity-account` policy, a fixed window per client IP (default 30 requests per minute), answering `429` with `Retry-After`. Counters are kept in memory per instance. Identity applies its own limits as well.
- **Its own `/health` liveness,** never proxied. Problem details for errors, and Serilog request logging with YARP logs at Warning.
- **No token validation and no authorization.** Services authorize every request themselves (ADR-007, ADR-017 section 4).

**Verification:**

- **Tested:**
  - internal-path blocking, including through prefix-removing and rewriting routes, and the forwarding of other paths (`EdgeRoutingTests`)
  - the committed Development route set (`DevelopmentRouteTests`)
  - every committed route on real Kestrel with a recording backend, across hosts and alternate path forms (`InternalPathExposureTests`; [PHASE_3_TENANT_MERCHANT.md section 10](PHASE_3_TENANT_MERCHANT.md#10-findings-residual-risks-and-production-gates), P3-1)
- **Tested on the Identity side only:** Identity's trust of the Gateway's forwarded headers is tested against a stand-in proxy (`TrustedProxyTests`), not through the real Gateway.
- **Not covered by an automated test:** the rate limiter.
- **Not verified:** the full local flow through the Gateway (finding I29).

**Deferred** (deployment concerns, not implemented):

- production routes and clusters: `appsettings.json` defines none
- public host names and TLS certificates per environment (locally, finding I29)
- the Gateway's network address configured as the only trusted proxy of Identity and the BFFs
- a rate-limit store shared across Gateway instances
- optional early token validation, which ADR-017 section 4 allows
