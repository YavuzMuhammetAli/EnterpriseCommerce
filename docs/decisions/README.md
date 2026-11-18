# Architecture Decision Records

Each ADR records one significant decision: its context, the decision itself, its consequences and the alternatives that were considered. ADRs are historical records. Once accepted, their decision text is not rewritten. A changed decision gets a new ADR that supersedes or amends the old one, and the old ADR's status line points to the new one.

The consolidated, current view of all decisions is [PROJECT_DECISIONS.md](../architecture/PROJECT_DECISIONS.md).

| ADR | Title | Status | Superseded / amended by |
|---|---|---|---|
| [ADR-001](ADR-001-microservices-architecture.md) | Microservices Architecture | Accepted | |
| [ADR-002](ADR-002-clean-architecture-per-service.md) | Clean Architecture per Service | Accepted | |
| [ADR-003](ADR-003-database-per-service.md) | Database per Service | Accepted | |
| [ADR-004](ADR-004-sql-server-and-mongodb-selection.md) | SQL Server and MongoDB Selection | Accepted (rule 2 superseded) | ADR-011 |
| [ADR-005](ADR-005-kafka-for-asynchronous-communication.md) | Kafka for Asynchronous Communication | Accepted (amended) | ADR-012 |
| [ADR-006](ADR-006-redis-usage-boundaries.md) | Redis Usage Boundaries | Accepted (rule 4 superseded) | ADR-011 |
| [ADR-007](ADR-007-yarp-api-gateway.md) | YARP API Gateway | Accepted | |
| [ADR-008](ADR-008-docker-compose-first.md) | Docker Compose First | Accepted | |
| [ADR-009](ADR-009-selective-cqrs.md) | Selective CQRS | Accepted | |
| [ADR-010](ADR-010-no-mandatory-event-sourcing.md) | No Mandatory Event Sourcing | Accepted | |
| [ADR-011](ADR-011-per-service-data-store-assignment.md) | Per-Service Data Store Assignment | Accepted | |
| [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md) | Transactional Outbox and Asynchronous Commands | Accepted (direction; implementation deferred) | |
| [ADR-013](ADR-013-saga-based-workflow-coordination.md) | Saga-Based Workflow Coordination | Accepted (direction; detailed design deferred) | |
| [ADR-014](ADR-014-logical-tenant-isolation.md) | Logical Tenant Isolation | Accepted | |
| [ADR-015](ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md) | Identity Provider: ASP.NET Core Identity with OpenIddict | Accepted (amended) | ADR-017 |
| [ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md) | Browser Client Architecture: Backend-for-Frontend | Accepted | |
| [ADR-017](ADR-017-token-session-and-protocol-strategy.md) | Token, Session and Protocol Strategy | Accepted | |
| [ADR-018](ADR-018-account-security-tokens-and-security-email-delivery.md) | Account Security Tokens and Security Email Delivery | Accepted | |

New ADRs use the next number and the same four sections: Context, Decision, Consequences and Alternatives considered.
