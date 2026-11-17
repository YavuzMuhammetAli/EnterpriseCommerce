# Service Catalog

EnterpriseCommerce is a multi-tenant, multi-vendor marketplace made up of **exactly ten business microservices** and one infrastructure component (the YARP API Gateway). Adding a business service (for example Customer, Pricing or Reporting) requires an explicit architecture review and an ADR.

Each service owns its data exclusively ([ADR-003](../decisions/ADR-003-database-per-service.md)). The data store mapping is defined in [ADR-011](../decisions/ADR-011-per-service-data-store-assignment.md). The consolidated decisions are in [PROJECT_DECISIONS.md](PROJECT_DECISIONS.md).

The responsibilities below set boundaries, not detailed designs. Entities, APIs and events are designed per service in later phases.

## Overview

| # | Service | Planned project prefix | Data store | Status |
|---|---|---|---|---|
| 1 | Identity | `EnterpriseCommerce.Identity` | SQL Server | Skeleton (Phase 1); design is Phase 2 |
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
- Open decisions: the identity protocol and provider, the token and session strategy, and the data model. These are scoped in [PHASE_2_IDENTITY_DESIGN_BRIEF.md](PHASE_2_IDENTITY_DESIGN_BRIEF.md).
- API baseline: [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md) (Phase 0.3, subject to Phase 2 review).
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
