# Architecture Decision Records

Each ADR records one significant decision: its context, the decision itself, its consequences and the alternatives that were considered. ADRs are historical records. Once accepted, their decision text is not rewritten. A changed decision gets a new ADR that supersedes or amends the old one, and the old ADR's status line points to the new one.

The consolidated, current view of all decisions is [PROJECT_DECISIONS.md](../architecture/PROJECT_DECISIONS.md).

| ADR | Title | Status | Superseded / amended by |
|---|---|---|---|
| [ADR-001](ADR-001-microservices-architecture.md) | Microservices Architecture | Accepted | |
| [ADR-002](ADR-002-clean-architecture-per-service.md) | Clean Architecture per Service | Accepted | |
| [ADR-003](ADR-003-database-per-service.md) | Database per Service | Accepted | |
| [ADR-004](ADR-004-sql-server-and-mongodb-selection.md) | SQL Server and MongoDB Selection | Accepted (rule 2 superseded) | ADR-011 |
| [ADR-005](ADR-005-kafka-for-asynchronous-communication.md) | Kafka for Asynchronous Communication | Accepted (amended; deferred items resolved) | ADR-012; ADR-023 (client library, reliable publishing) |
| [ADR-006](ADR-006-redis-usage-boundaries.md) | Redis Usage Boundaries | Accepted (rule 4 superseded) | ADR-011 |
| [ADR-007](ADR-007-yarp-api-gateway.md) | YARP API Gateway | Accepted | |
| [ADR-008](ADR-008-docker-compose-first.md) | Docker Compose First | Accepted | |
| [ADR-009](ADR-009-selective-cqrs.md) | Selective CQRS | Accepted | |
| [ADR-010](ADR-010-no-mandatory-event-sourcing.md) | No Mandatory Event Sourcing | Accepted | |
| [ADR-011](ADR-011-per-service-data-store-assignment.md) | Per-Service Data Store Assignment | Accepted | |
| [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md) | Transactional Outbox and Asynchronous Commands | Accepted (direction; deferred items resolved) | ADR-023 (implementation design) |
| [ADR-013](ADR-013-saga-based-workflow-coordination.md) | Saga-Based Workflow Coordination | Accepted (direction; detailed design deferred) | |
| [ADR-014](ADR-014-logical-tenant-isolation.md) | Logical Tenant Isolation | Accepted (open naming item resolved) | ADR-020 (naming) |
| [ADR-015](ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md) | Identity Provider: ASP.NET Core Identity with OpenIddict | Accepted (amended) | ADR-017, ADR-019 (R7 retirement period) |
| [ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md) | Browser Client Architecture: Backend-for-Frontend | Accepted | |
| [ADR-017](ADR-017-token-session-and-protocol-strategy.md) | Token, Session and Protocol Strategy | Accepted (amended) | ADR-019 (section 12 key rotation) |
| [ADR-018](ADR-018-account-security-tokens-and-security-email-delivery.md) | Account Security Tokens and Security Email Delivery | Accepted | |
| [ADR-019](ADR-019-signing-and-encryption-key-rollover-and-retirement.md) | Signing and Encryption Key Rollover and Retirement | Accepted | |
| [ADR-020](ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md) | Tenant & Merchant Naming and Tenant-Merchant Cardinality | Accepted | |
| [ADR-021](ADR-021-merchant-invitations-and-membership-administration.md) | Merchant Invitations and Membership Administration | Accepted (amended) | ADR-022 (section 4 owner rule) |
| [ADR-022](ADR-022-self-service-tenant-onboarding.md) | Self-Service Tenant Onboarding | Accepted | |
| [ADR-023](ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) | Integration Event Publishing and the Transactional Outbox Implementation | Accepted | |
| [ADR-024](ADR-024-catalog-service-design.md) | Catalog Service Design | Accepted | |
| [ADR-025](ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md) | Merchant Authorization through Tenant & Merchant Membership Verification | Accepted | |

New ADRs use the next number and the same four sections: Context, Decision, Consequences and Alternatives considered.
