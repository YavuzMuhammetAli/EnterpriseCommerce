# Service Catalog

EnterpriseCommerce is a multi-tenant, multi-vendor marketplace made up of **exactly ten business microservices** and one infrastructure component (the YARP API Gateway). Adding a business service (for example Customer, Pricing or Reporting) requires an explicit architecture review and an ADR.

Each service owns its data exclusively ([ADR-003](../decisions/ADR-003-database-per-service.md)). The data store mapping is defined in [ADR-011](../decisions/ADR-011-per-service-data-store-assignment.md). The consolidated decisions are in [PROJECT_DECISIONS.md](PROJECT_DECISIONS.md).

The responsibilities below set boundaries, not detailed designs. Entities, APIs and events are designed per service in later phases.

## Overview

| # | Service | Planned project prefix | Data store | Status |
|---|---|---|---|---|
| 1 | Identity | `EnterpriseCommerce.Identity` | SQL Server | Design accepted (Phase 2.2); implementation in Phase 2.3 |
| 2 | Tenant & Merchant | `EnterpriseCommerce.TenantMerchant` *(provisional name)* | SQL Server | Planned |
| 3 | Catalog | `EnterpriseCommerce.Catalog` | MongoDB | Planned |
| 4 | Inventory | `EnterpriseCommerce.Inventory` | SQL Server | Planned |
| 5 | Cart | `EnterpriseCommerce.Cart` | SQL Server + Redis (acceleration only) | Planned |
| 6 | Order | `EnterpriseCommerce.Order` | SQL Server | Planned |
| 7 | Payment | `EnterpriseCommerce.Payment` | SQL Server | Planned |
| 8 | Fulfillment | `EnterpriseCommerce.Fulfillment` | SQL Server | Planned |
| 9 | Notification | `EnterpriseCommerce.Notification` | SQL Server | Planned |
| 10 | Admin | `EnterpriseCommerce.Admin` | SQL Server | Planned |
| — | API Gateway (infrastructure) | `EnterpriseCommerce.Gateway` | none | Skeleton (Phase 1) |
| — | Backend-for-frontend (infrastructure, one codebase, one deployment per front end) | decided in the Phase 2.3 BFF slice | Dedicated Redis for session tickets (ephemeral) | Accepted ([ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md)); not yet implemented |

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
- Open: the detailed data model, designed in the Phase 2.3 implementation within these ADRs.
- API contract: [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md).
- Phase 1 state: a compilable four-layer skeleton with a health endpoint, OpenAPI, exception handling and structured logging. **No identity features.**

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
- Security emails (confirmation and password reset links) arrive through an authenticated internal endpoint called by Identity with a service token, never through Kafka. Notification never persists the link or the rendered body of a security email ([ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)).

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
