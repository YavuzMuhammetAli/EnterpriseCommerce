# Service Catalog

EnterpriseCommerce is a multi-tenant, multi-vendor marketplace made up of ten business microservices and one infrastructure component (the YARP API Gateway).

Each service owns its data exclusively ([ADR-003](../decisions/ADR-003-database-per-service.md)). The data store for each service is **decided in that service's design phase** under the rules in [ADR-004](../decisions/ADR-004-sql-server-and-mongodb-selection.md) and [ADR-006](../decisions/ADR-006-redis-usage-boundaries.md). Until then it is listed as *to be decided*.

The responsibilities below set boundaries, not detailed designs. Entities, APIs and events are designed per service in later phases.

## Overview

| # | Service | Planned project prefix | Status |
|---|---|---|---|
| 1 | Identity | `EnterpriseCommerce.Identity` | Skeleton (Phase 1) |
| 2 | Tenant & Merchant | `EnterpriseCommerce.TenantMerchant` *(provisional name)* | Planned |
| 3 | Catalog | `EnterpriseCommerce.Catalog` | Planned |
| 4 | Inventory | `EnterpriseCommerce.Inventory` | Planned |
| 5 | Cart | `EnterpriseCommerce.Cart` | Planned |
| 6 | Order | `EnterpriseCommerce.Order` | Planned |
| 7 | Payment | `EnterpriseCommerce.Payment` | Planned |
| 8 | Fulfillment | `EnterpriseCommerce.Fulfillment` | Planned |
| 9 | Notification | `EnterpriseCommerce.Notification` | Planned |
| 10 | Admin | `EnterpriseCommerce.Admin` | Planned |
| — | API Gateway (infrastructure) | `EnterpriseCommerce.Gateway` | Skeleton (Phase 1) |

## 1. Identity

**Responsibility:** user identities and authentication for customers, merchant staff and platform administrators.

- Owns: user accounts, credentials, authentication sessions and tokens, and identity-level role/claim assignments.
- Does not own: tenant or merchant profiles (Tenant & Merchant), or customer order history (Order).
- Open decisions: the identity protocol and provider (for example, a self-hosted OpenID Connect server or an external identity provider), the token format, and the refresh token strategy. These are deferred to a dedicated Identity design step.
- Phase 1 state: compilable four-layer skeleton with a health endpoint, OpenAPI, exception handling and structured logging. **No identity features.**

## 2. Tenant & Merchant

**Responsibility:** the organizational structure of the marketplace, meaning tenants and the merchants (vendors) that sell on it.

- Owns: tenant records and settings, merchant onboarding and profiles, merchant status (for example pending, active, suspended), and membership of users in merchants.
- Does not own: user credentials (Identity) or products (Catalog).
- Open decisions: the precise tenancy model, meaning how tenant, merchant and user relate and how tenant isolation is enforced across services. The final project name is also open (`TenantMerchant` is provisional).

## 3. Catalog

**Responsibility:** what can be sold, and how it is described and found.

- Owns: products and their merchant-specific listings, categories and product attributes.
- Does not own: stock levels (Inventory) or order-time price snapshots (Order).
- Notes: product data shapes may vary by category. That is relevant to the store decision under ADR-004 and is decided in the Catalog design phase.

## 4. Inventory

**Responsibility:** stock availability per merchant and sellable item.

- Owns: stock levels, and stock reservations and releases for checkout.
- Does not own: product descriptions (Catalog) or order state (Order).
- Notes: consistency of stock under concurrent checkouts is a core design concern of the Inventory phase.

## 5. Cart

**Responsibility:** a shopper's in-progress selection before checkout.

- Owns: carts and cart items.
- MVP constraint: a cart checks out with items from a **single merchant**. Multi-merchant checkout is a later phase.
- Does not own: prices of record or stock (it reads them from, or validates them against, Catalog and Inventory).

## 6. Order

**Responsibility:** orders from placement through completion or cancellation.

- Owns: orders, order lines (including price snapshots taken at order time), and order status history.
- Coordinates the checkout outcome with Inventory and Payment. The coordination mechanism (for example, a saga) is designed in the checkout phase and is out of scope for Phase 1.

## 7. Payment

**Responsibility:** payment authorization and capture for orders, and refunds.

- Owns: payment records and payment status.
- **Simulated initially.** No real payment provider is integrated. **Card data is never collected, transmitted or stored** by the platform.
- Open decisions: real provider integration (a later phase) and tokenization approach.

## 8. Fulfillment

**Responsibility:** getting a paid order to the customer.

- Owns: fulfillment and shipment records and their status (for example awaiting shipment, shipped, delivered).
- Does not own: order lifecycle state (Order). It publishes fulfillment progress that Order and Notification may consume.

## 9. Notification

**Responsibility:** delivering messages to users in reaction to events from other services.

- Owns: notification templates, delivery attempts and the delivery log.
- Mostly event-driven. It must not become a place where other services' business rules live.
- Open decisions: delivery channels and providers.

## 10. Admin

**Responsibility:** platform operator back-office capabilities, such as oversight, moderation and support tooling.

- Owns: only admin-specific data (for example, audit notes and moderation decisions).
- Does **not** read other services' databases. It works through their APIs and events like any other client. It must not become a shared business layer.

## API Gateway (infrastructure)

`EnterpriseCommerce.Gateway` is a YARP reverse proxy, the single external entry point ([ADR-007](../decisions/ADR-007-yarp-api-gateway.md)). It routes and applies edge concerns only, and contains no business logic. See [SOLUTION_STRUCTURE.md](SOLUTION_STRUCTURE.md#gateway-responsibility).
