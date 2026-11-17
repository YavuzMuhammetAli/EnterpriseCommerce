# ADR-004: SQL Server and MongoDB Selection

- **Status:** Accepted. Rule 2 (per-service assignment left open) is superseded by [ADR-011](ADR-011-per-service-data-store-assignment.md).
- **Date:** 2026-11-17

## Context

With database per service ([ADR-003](ADR-003-database-per-service.md)), each service may choose the store that fits its data. Unlimited choice would raise operational and learning cost, so the platform needs a small, approved set of stores. Some data is strongly relational and transactional (for example, orders and payments). Some is document-shaped with variable structure or read-optimized views.

## Decision

The platform's approved primary data stores are:

- **SQL Server**, the default for transactional and relational data, accessed through **Entity Framework Core**. It is used where strong consistency, relational integrity and transactions within a service matter.
- **MongoDB**, for document-shaped data such as variable-structure documents, denormalized read models and content where a fixed relational schema is a poor fit.

Rules:

1. A service uses SQL Server unless its design phase shows a document model fits its data better.
2. **Per-service store assignment is not decided by this ADR.** It is an open decision for each service's design phase and is recorded in that service's documentation (or a follow-up ADR) with its reasoning.
3. A service may use more than one store only with explicit justification.
4. Redis is not a primary data store ([ADR-006](ADR-006-redis-usage-boundaries.md)).
5. Store-specific code (EF Core, MongoDB driver) lives only in the Infrastructure layer.

Local development uses SQL Server 2022 (Developer edition) and MongoDB 8.0 containers with pinned versions ([ADR-008](ADR-008-docker-compose-first.md)).

## Consequences

- Positive: two well-supported stores cover both the relational and the document use cases. EF Core gives productive, well-known data access for the default case.
- Positive: deferring the per-service assignment avoids committing before each domain is understood.
- Negative: two database technologies to operate, back up, monitor and learn.
- Negative: SQL Server Developer edition is for development only. Production licensing or a managed offering is a deployment-phase concern.
- Negative: SQL Server containers are memory-heavy (at least 2 GB), which is mitigated with Compose profiles and memory limits.

## Alternatives considered

- **PostgreSQL** in place of SQL Server: an excellent open-source option with JSONB support that could cover both models. SQL Server was chosen as an agreed project constraint, reflecting its prevalence in .NET enterprise environments.
- **SQL Server only** (JSON columns for documents): fewer technologies, but weaker document querying and indexing ergonomics for document-heavy services.
- **MongoDB only:** weak fit for transactional, relational data such as orders and payments.
- **Azure Cosmos DB or other managed stores:** they tie local development to a cloud service. They could be reconsidered at deployment time.
