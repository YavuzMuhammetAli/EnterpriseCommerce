# ADR-003: Database per Service

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

Shared databases create hidden coupling between services. Schema changes ripple across teams, services can bypass each other's business rules, and independent deployment becomes impossible. Microservices ([ADR-001](ADR-001-microservices-architecture.md)) only deliver their benefits when each service controls its own data.

## Decision

Each microservice **exclusively owns its data**.

- No service reads or writes another service's database, schema, tables or collections, not even for reporting or "just one query".
- Data owned by another service is obtained through that service's API, or kept as a local read model built from its integration events ([ADR-005](ADR-005-kafka-for-asynchronous-communication.md)).
- Each service has its own database (and, when deployed, its own credentials). In local development, several services may share one SQL Server or MongoDB **server instance** to save resources, but each uses a separate database with no cross-database queries.
- Schema migrations are owned and applied by the owning service.

## Consequences

- Positive: services can evolve their schema and storage independently.
- Positive: business invariants are enforced in one place, the owning service.
- Negative: no cross-service joins or distributed transactions. Workflows that span services need eventual consistency, idempotent handlers and explicit compensation (designed in later phases, for example checkout).
- Negative: some data is duplicated across services (for example, price snapshots in Order), and the duplication must be deliberate and documented.
- Negative: cross-service reporting needs a dedicated read model or analytics pipeline in a later phase.

## Alternatives considered

- **Shared database with schema per service:** easier joins, but it allows coupling through the database and makes the boundary unenforceable. Rejected.
- **Shared database, shared tables:** rejected outright, because it removes data ownership.
- **Distributed transactions (2PC/MSDTC):** poor availability and not supported across the chosen data stores and the broker. Rejected in favour of eventual consistency.
