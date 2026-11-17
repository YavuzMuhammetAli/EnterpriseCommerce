# ADR-010: No Mandatory Event Sourcing

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

Event sourcing stores an aggregate's state as an append-only sequence of events instead of its current state. It provides a complete audit history and temporal queries, and it pairs naturally with event-driven architectures. It also brings significant costs: event schema versioning, snapshotting, projection rebuilds, more complex querying, and an unfamiliar programming model.

The platform already uses domain events internally and integration events over Kafka ([ADR-005](ADR-005-kafka-for-asynchronous-communication.md)). Neither of these requires event sourcing.

## Decision

Event sourcing is **not mandatory** and is **not the default** persistence model.

- Services persist the **current state** of their aggregates in their own data store ([ADR-003](ADR-003-database-per-service.md), [ADR-004](ADR-004-sql-server-and-mongodb-selection.md)).
- Domain events and integration events are used without event-sourced persistence.
- Audit and history requirements are first addressed with conventional means (status history tables, audit records).
- A specific service or aggregate may adopt event sourcing only through a dedicated ADR. That ADR must show a concrete requirement (for example, a legally required complete history or temporal queries central to the domain) that conventional persistence can't meet reasonably.

## Consequences

- Positive: simpler persistence, querying and onboarding, and EF Core / MongoDB can be used conventionally.
- Positive: avoids event schema versioning and projection rebuild complexity where it brings no business value.
- Negative: full historical state reconstruction isn't available by default. Where history matters, it must be modelled explicitly.
- Negative: if a service later needs event sourcing, migrating existing state-based data requires a deliberate migration plan.

## Alternatives considered

- **Event sourcing for all services:** maximal auditability, but high complexity across the whole platform without a matching requirement. Rejected.
- **Event sourcing for selected core domains (for example, Order or Payment) from the start:** possible later, but not justified by any current requirement. Deferred to per-service ADRs if needed.
