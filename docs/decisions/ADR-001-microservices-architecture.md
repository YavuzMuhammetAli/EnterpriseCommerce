# ADR-001: Microservices Architecture

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

EnterpriseCommerce is a multi-tenant, multi-vendor marketplace. Its business capabilities (identity, tenants and merchants, catalog, inventory, cart, order, payment, fulfillment, notification and administration) have different change rates, load profiles and consistency needs. Catalog browsing is read-heavy, checkout is consistency-critical, and notifications are asynchronous and failure-tolerant.

The project is also a long-term, portfolio-grade system that should demonstrate production-inspired practices for distributed systems: independent deployability, data ownership, asynchronous integration and observability.

## Decision

Build the system as **ten business microservices**: Identity, Tenant & Merchant, Catalog, Inventory, Cart, Order, Payment, Fulfillment, Notification and Admin. A **YARP API Gateway** acts as an infrastructure component in front of them.

- Each service is independently buildable, testable and deployable.
- Each service owns its data ([ADR-003](ADR-003-database-per-service.md)).
- Services integrate through HTTP APIs and asynchronous integration events ([ADR-005](ADR-005-kafka-for-asynchronous-communication.md)), never through shared databases or shared domain code.
- Services are introduced incrementally. Phase 1 creates only the Identity skeleton and the Gateway. The MVP supports single-merchant checkout, and multi-merchant checkout follows later.

## Consequences

- Positive: clear ownership boundaries; services can scale and evolve independently; failure isolation is possible (for example, Notification outages don't block checkout).
- Positive: the architecture exercises realistic distributed-system concerns, which is a stated goal of the project.
- Negative: much higher operational and cognitive complexity than a monolith, including distributed debugging, network failures, eventual consistency, contract versioning and local environment cost.
- Negative: cross-service workflows (checkout) need explicit coordination, idempotency and failure handling. These are designed in their own phases.
- Mitigations: strict boundaries enforced by architecture tests, Docker Compose profiles to limit local resource use ([ADR-008](ADR-008-docker-compose-first.md)), and a dedicated observability phase.

## Alternatives considered

- **Modular monolith:** a single deployable with strongly separated modules. It is simpler to operate and would suit many real teams at this scale. It was rejected because the project deliberately targets the independent deployment, data ownership and asynchronous integration concerns of a microservices system. The per-service layering keeps a later consolidation possible if needed.
- **Classic layered monolith:** rejected. It provides weak boundaries between business capabilities and doesn't meet the project goals.
- **Fewer, coarser services** (for example, merging Cart and Order): could be revisited per boundary, but the ten capabilities above are the agreed baseline.
