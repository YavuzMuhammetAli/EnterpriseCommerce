# ADR-011: Per-Service Data Store Assignment

- **Status:** Accepted
- **Date:** 2026-11-17
- **Supersedes:** [ADR-004](ADR-004-sql-server-and-mongodb-selection.md) rule 2 (per-service assignment left open) and [ADR-006](ADR-006-redis-usage-boundaries.md) rule 4 (Cart's use of Redis left open)

## Context

[ADR-004](ADR-004-sql-server-and-mongodb-selection.md) approved SQL Server and MongoDB as the platform's primary data stores. It deliberately left each service's store to be decided in that service's design phase. [ADR-006](ADR-006-redis-usage-boundaries.md) set the boundaries for Redis and also left open whether Cart uses it.

The Phase 0 architecture agreement already contained a per-service mapping. The Phase 1 audit found that the Phase 1 ADRs recorded this mapping as open, which contradicts the agreement. This ADR records the agreed mapping so that service designs start from it.

## Decision

Each service is assigned the following primary data store:

| Service | Data store |
|---|---|
| Identity | SQL Server |
| Tenant & Merchant | SQL Server |
| Catalog | MongoDB |
| Inventory | SQL Server |
| Cart | SQL Server (durable store) + Redis (acceleration only) |
| Order | SQL Server |
| Payment | SQL Server |
| Fulfillment | SQL Server |
| Notification | SQL Server |
| Admin | SQL Server |

Rules that come with this mapping:

1. **Catalog uses MongoDB.** Product data has category-dependent attributes and is read-heavy, and the document model fits that shape.
2. **Cart's durable store is SQL Server.** Redis may speed up cart access and short-lived operations. Cart must stay correct if Redis is flushed or unavailable, in line with [ADR-006](ADR-006-redis-usage-boundaries.md) rule 1.
3. **Inventory correctness relies on SQL Server** transactions, concurrency control and unique reservation rules. Redis locks must never be the source of truth for stock or reservations.
4. Redis is never the source of truth for orders or payments.
5. Database per service ([ADR-003](ADR-003-database-per-service.md)) still applies. In local development, one SQL Server instance hosts several logically separate service databases. That does not remove service-level data ownership.
6. Detailed per-service schemas are **not** decided here. They are designed in each service's phase.

All other rules of ADR-004 and ADR-006 remain in force.

## Consequences

- Positive: service designs start from a known store, so phases no longer re-open the same question.
- Positive: most services share one database technology (SQL Server with EF Core), which keeps tooling and skills consistent.
- Negative: Catalog is the only MongoDB service, so MongoDB expertise and operations serve one service. This is accepted because of the fit for product data.
- Negative: Cart has two stores. The Cart design must define what is cached in Redis, the invalidation strategy, and behaviour when Redis is unavailable.
- Changing a service's store later requires a new ADR.

## Alternatives considered

- **Keep the assignment open per service (ADR-004 rule 2):** this was the Phase 1 position. It is rejected because it contradicts the agreed Phase 0 architecture and would let each phase re-decide independently.
- **Catalog on SQL Server (JSON columns for attributes):** fewer technologies, but less natural for variable product attributes and document-shaped reads.
- **Cart in Redis only:** fast and simple, but it would make Redis a system of record, which ADR-006 forbids. Carts feed checkout, so their contents must be durable.
- **Inventory reservations coordinated through Redis locks:** rejected. Distributed locks are not a correctness guarantee for stock, and the database must enforce it.
