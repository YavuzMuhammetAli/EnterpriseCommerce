# Project Decisions

**This is the canonical, consolidated reference** for the architectural and technical decisions of EnterpriseCommerce. It combines the Phase 0 architecture agreement, the design work between Phase 0 and Phase 1, and the Phase 1 ADRs.

How this document relates to the others:

- **Architecture Decision Records** ([docs/decisions](../decisions/README.md)) are the historical records of individual decisions, including their alternatives. This document summarizes the *current* outcome and links to them.
- **[CLAUDE.md](../../CLAUDE.md)** is the operational instruction manual for implementation sessions.
- **[SERVICE_CATALOG.md](SERVICE_CATALOG.md)** describes service responsibilities in more detail.

**Change policy:** a decision in this document changes only through a new ADR that supersedes or amends the old one. This document is then updated in the same change. If this document and an ADR disagree, stop and resolve the inconsistency. Never guess.

Last consolidated: 2026-11-17 (Phase 1 final audit). Updated 2026-11-17 for Phase 2.1 (Identity provider selection, [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)).

---

## 1. Product vision

EnterpriseCommerce is a portfolio-grade, enterprise-oriented, **multi-tenant and multi-vendor e-commerce marketplace** built with modern .NET technologies.

The platform supports:

- platform-level administration
- multiple merchants
- merchant employees with role-based access
- customers
- product catalog management
- inventory management
- shopping carts
- order processing
- payment simulation
- fulfillment
- notifications
- platform governance and auditing

The architecture prioritizes **maintainability, scalability, security, separation of concerns, testability and operational reliability**. No specific performance or throughput targets have been set. Any such claim requires measurement.

## 2. Technology decisions

| Area | Decision | Status |
|---|---|---|
| Runtime | .NET 10 LTS | In use |
| API | ASP.NET Core Web API | In use |
| ORM | Entity Framework Core | Planned (first persistence phase) |
| Relational database | SQL Server | Local container available |
| Document database | MongoDB | Local container available |
| Cache | Redis | Local container available |
| Messaging | Apache Kafka (KRaft) | Local container available |
| API Gateway | YARP | In use |
| Identity provider | ASP.NET Core Identity (user store) with OpenIddict (OAuth 2.0 / OpenID Connect server), [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md) | Accepted; not yet implemented |
| Containers | Docker | In use |
| Local orchestration | Docker Compose (profiles) | In use |
| Logging | Serilog | In use |
| Telemetry | OpenTelemetry | Planned (observability phase) |
| Metrics | Prometheus | Planned (observability phase) |
| Dashboards | Grafana | Planned (observability phase) |
| Log aggregation | Loki | Planned (observability phase) |
| Testing | xUnit (v3) | In use |
| CI/CD | GitHub Actions | Workflow defined, not yet executed (no remote) |
| Source control | Git | In use |

Rules:

- Prometheus, Grafana, Loki and the OpenTelemetry Collector are introduced in a later observability phase, not in the initial infrastructure.
- Docker Compose profiles control resource consumption ([ADR-008](../decisions/ADR-008-docker-compose-first.md)).
- No alternative technology (for example PostgreSQL or RabbitMQ) is introduced without an explicit review and an ADR.
- Packages are added only for a concrete need, after evaluating maintenance status, compatibility and licensing.

## 3. Architecture style

Accepted principles:

| Principle | Reference |
|---|---|
| Microservices architecture | [ADR-001](../decisions/ADR-001-microservices-architecture.md) |
| Domain-Driven Design where it adds real value | [ADR-002](../decisions/ADR-002-clean-architecture-per-service.md) |
| Clean Architecture within each business service | [ADR-002](../decisions/ADR-002-clean-architecture-per-service.md) |
| Selective CQRS | [ADR-009](../decisions/ADR-009-selective-cqrs.md) |
| Transactional Outbox (direction; implementation deferred) | [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md) |
| Sagas: orchestration or choreography where appropriate | [ADR-013](../decisions/ADR-013-saga-based-workflow-coordination.md) |
| Database per service | [ADR-003](../decisions/ADR-003-database-per-service.md) |
| API Gateway using YARP | [ADR-007](../decisions/ADR-007-yarp-api-gateway.md) |
| Event-driven communication through Kafka | [ADR-005](../decisions/ADR-005-kafka-for-asynchronous-communication.md), [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md) |
| REST for synchronous API interactions | this document |
| Strong service ownership and independent deployment boundaries | [ADR-001](../decisions/ADR-001-microservices-architecture.md), [ADR-003](../decisions/ADR-003-database-per-service.md) |

Constraints:

- **Event Sourcing is not part of the accepted architecture** and is never forced ([ADR-010](../decisions/ADR-010-no-mandatory-event-sourcing.md)).
- CQRS is not applied to every CRUD operation.
- No unnecessary abstractions. No generic repository for every table without a clear reason.
- **No shared business-domain models between services.** BuildingBlocks holds technical code only, and only when needed ([SOLUTION_STRUCTURE.md](SOLUTION_STRUCTURE.md#buildingblocks-policy)).

## 4. Clean Architecture rules

Each business service follows this structure where appropriate. Dependencies always point inward. The rules are enforced by `tests/ArchitectureTests` (declared and compiled references).

| Layer | Contains | May depend on |
|---|---|---|
| **Domain** | Entities, aggregates, value objects, domain events, business invariants | Nothing. No EF Core, SQL Server, MongoDB, Kafka, Redis, ASP.NET Core or other infrastructure |
| **Application** | Use cases, commands and queries where appropriate, application interfaces, validation, application-level orchestration | Domain |
| **Infrastructure** | Database access, EF Core configurations, repositories when justified, Kafka and Redis adapters, external integrations | Application, Domain |
| **API** | HTTP endpoints, request and response models, middleware, authentication and authorization configuration, DI composition root | Application, Infrastructure |

A service never references another service's projects. The Gateway references no service project. Details: [SOLUTION_STRUCTURE.md](SOLUTION_STRUCTURE.md).

## 5. Business microservices

There are **exactly ten** planned business microservices. **YARP Gateway is infrastructure, not an eleventh business service.** No additional business service (for example Customer, Pricing or Reporting) is added without an explicit architecture review and decision. Full descriptions: [SERVICE_CATALOG.md](SERVICE_CATALOG.md).

| # | Service | Responsible for |
|---|---|---|
| 1 | **Identity** | user identity, credentials, authentication, token issuance and validation responsibilities, the refresh token lifecycle, sessions, password management, and identity-related security processes. *Does not own tenant or merchant lifecycle management.* |
| 2 | **Tenant & Merchant** | the tenant lifecycle, merchant registration and onboarding, merchant profiles, merchant membership, merchant employee invitations, merchant membership roles, and subscription and usage management |
| 3 | **Catalog** | product information and descriptions, categories, product attributes, the product publication lifecycle, and catalog search and retrieval |
| 4 | **Inventory** | stock levels and adjustments, inventory reservations and reservation expiration, stock availability, and inventory concurrency control |
| 5 | **Cart** | shopping carts, cart items, the cart lifecycle, and cart validation and expiration |
| 6 | **Order** | the order lifecycle, checkout orchestration, order snapshots, order state transitions, checkout failure handling, and cancellation workflows |
| 7 | **Payment** | payment authorization simulation, payment status, payment transaction records, payment failure handling, and void and refund workflows |
| 8 | **Fulfillment** | shipment preparation, the shipment lifecycle, fulfillment status, tracking information, and delivery updates |
| 9 | **Notification** | email notification workflows, order- and account-related notifications, notification delivery status, and retry and failure handling |
| 10 | **Admin** | platform-level governance, platform administrator roles and assignments, platform policies, administrative operations, audit records, and platform-level monitoring and management operations. *Does not own user credentials or authentication sessions.* |

Service-specific rules:

- **Inventory:** correctness relies on database transactions, concurrency control and unique reservation rules. Redis locks are never the source of truth for inventory correctness.
- **Order:** orders preserve **immutable product and price snapshots** taken at checkout (product name, SKU, unit price, currency and quantity). Catalog price changes never modify historical orders.
- **Payment:** simulated only at first. Raw card data is **never collected, stored or processed**.

## 6. Data ownership and database mapping

Governing ADRs: [ADR-003](../decisions/ADR-003-database-per-service.md), [ADR-004](../decisions/ADR-004-sql-server-and-mongodb-selection.md), [ADR-006](../decisions/ADR-006-redis-usage-boundaries.md), [ADR-011](../decisions/ADR-011-per-service-data-store-assignment.md).

| Service | Database |
|---|---|
| Identity | SQL Server |
| Tenant & Merchant | SQL Server |
| Catalog | MongoDB |
| Inventory | SQL Server |
| Cart | SQL Server + Redis |
| Order | SQL Server |
| Payment | SQL Server |
| Fulfillment | SQL Server |
| Notification | SQL Server |
| Admin | SQL Server |

- Each service owns its data. **A service never accesses another service's database directly.**
- In local development, one SQL Server instance may host several logically separate service databases. This does not remove service-level data ownership.
- Redis is never a system of record. For Cart it accelerates access and short-lived operations, while SQL Server holds the durable cart. Redis is never the source of truth for orders or payments.
- Detailed per-service schemas are designed in each service's phase.

## 7. Multi-tenancy

Governing ADR: [ADR-014](../decisions/ADR-014-logical-tenant-isolation.md).

- The initial approach is **logical tenant isolation**. There is no database per tenant unless a later ADR changes this.
- Tenant identification is **explicit**, and tenant context is **validated**.
- Tenant ownership and membership are managed by **Tenant & Merchant**.
- **A tenant ID or JWT claim alone is not authorization proof.** Every service performs resource-level and tenant-level authorization for the resources it owns.
- Cross-tenant access is prevented. Tenant isolation is considered in queries, commands, events and background jobs.

**Decision status:**

- **The initial tenancy model is accepted. It is not an open decision.** That covers logical isolation, no database per tenant, Tenant & Merchant as owner of the tenant and merchant lifecycle, service-enforced tenant-level and resource-level authorization, and the rule that claims are not proof.
- **Open:** only the exact naming conventions, including whether `TenantMerchant` is the right project name (section 13).
- **Design-level details within the accepted model** are worked out in later phases and must conform to ADR-014. They are not open architectural decisions:
  - the tenant context claim shape (Phase 2 token strategy)
  - the membership data shared with other services (Tenant & Merchant design)
  - the per-service enforcement technique, for example EF Core query filters versus explicit scoping (each service's design)

## 8. Checkout and order processing

Governing ADR: [ADR-013](../decisions/ADR-013-saga-based-workflow-coordination.md).

**The MVP supports single-merchant checkout.** Multi-merchant checkout and merchant-specific fulfillment groups are advanced scope.

Planned checkout flow:

1. The customer initiates checkout.
2. Order validates the cart and the required product information.
3. Order creates a **pending (draft)** order.
4. Order stores immutable product and price snapshots.
5. Order initiates the inventory reservation workflow.
6. Inventory reserves stock.
7. Payment authorization is simulated.
8. The order becomes **confirmed** only after the workflow completes successfully.
9. Fulfillment and notification workflows begin.

Rules:

- There are four distinct states: **pending (draft)**, **confirmed**, **checkout failed** and **cancelled**.
- **`OrderConfirmed` is never published before checkout has completed successfully.**
- Checkout compensation and cancellation of an already confirmed order are **separate workflows**.
- Reservation expiration uses an explicit expiration mechanism and a corresponding event. The detailed design comes in a later phase.

## 9. Messaging and integration

Governing ADRs: [ADR-005](../decisions/ADR-005-kafka-for-asynchronous-communication.md), [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md).

- **Apache Kafka in KRaft mode** (no ZooKeeper) is the messaging platform.
- **REST** is used for synchronous interactions. **Kafka** is used for asynchronous integration events and commands.
- **Domain events** stay internal to their owning service. **Integration events** are the cross-service contracts.
- Event schemas are **versioned**. Consumers are **idempotent**.
- Retry, backoff, dead-letter handling and correlation identifiers are required considerations for every flow.
- **Transactional Outbox** is the accepted direction. **No outbox implementation is written before its detailed design phase.**
- The Kafka client library is **not selected** (open decision).
- Sensitive information is never published in integration events.

## 10. Security

- **Identity provider and protocol (accepted, [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)):**
  - The Identity service is the platform's **OAuth 2.0 / OpenID Connect** authorization server, built with **OpenIddict** and **ASP.NET Core Identity**, persisted in Identity's own SQL Server database.
  - Marketplace authorization is never delegated to OpenIddict.
  - ASP.NET Core Identity roles are **not authoritative** for platform roles (Admin) or merchant roles (Tenant & Merchant).
  - The Resource Owner Password Credentials grant and the implicit flow are **rejected**.
  - Client flows, the access token format, lifetimes and the browser client architecture (public SPA client versus backend-for-frontend) are decided in Phase 2.2. The recommendations are in ADR-015.
  - The Phase 0.3 API baseline and the proposed claims are in [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md).
- **Intended direction:** services validate JWTs **locally** (signature, issuer, audience, expiry). They don't call Identity on every request. The details are evaluated in Phase 2 ([section 7.4.1 of the brief](PHASE_2_IDENTITY_DESIGN_BRIEF.md#741-token-validation-architecture)).
- **Service-level authorization is mandatory.** Gateway authorization does not replace it.
- A tenant ID is not sufficient proof of access rights.
- Secrets are never committed. Local secrets live in a gitignored `.env`.
- Sensitive information is never published in integration events.
- Platform administration and merchant authorization are separate concerns.
- Payment is simulated, and raw card data is never handled.

## 11. MVP scope

**MVP:**

- customer registration and authentication
- merchant onboarding
- product creation and publication
- inventory initialization
- shopping cart
- single-merchant checkout
- inventory reservation
- simulated payment
- order confirmation
- shipment workflow
- notifications
- SuperAdmin tenant management
- administrative auditing

**Advanced (post-MVP):**

- multi-merchant checkout
- advanced saga recovery
- refund workflows
- advanced quotas and usage enforcement
- advanced search
- extended administrative operations
- full observability stack
- load testing
- Kubernetes
- advanced CI/CD

## 12. Local development constraints

- Windows development environment with Docker Desktop on WSL2.
- Limited memory: the machine has 16 GB, and the Docker VM had about 3.8 GB available at the time of the Phase 1 audit.
- Compose profiles allow each infrastructure component to start independently. **Running all infrastructure at the same time is never required.**
- Persistent named volumes, health checks, and ports bound to `127.0.0.1`.
- Local secrets live in `.env` (gitignored). `.env.example` holds documented placeholders only. Real secrets are never committed.

Details: [deploy/docker/README.md](../../deploy/docker/README.md).

## 13. Explicitly deferred decisions

These are **open**. They must not be decided silently. Each one is decided through the indicated phase and recorded in an ADR.

| Decision | Planned resolution |
|---|---|
| Detailed Identity data model | Phase 2 |
| Exact token and session strategy (lifetimes, refresh token rotation parameters, revocation behaviour, claim set) | Phase 2.2 |
| Browser client architecture: public SPA client versus backend-for-frontend, per front end (a backend-for-frontend needs its own ADR) | Phase 2.2 |
| Access token format and resource-server token validation approach | Phase 2.2 |
| Signing key storage and rotation, and Data Protection key storage, outside local development | Phase 2.2 (direction); deployment phase (concrete store) |
| Kafka client library | First event-flow phase |
| Outbox implementation details | First event-flow phase |
| Mediator library selection (if any) | When the first service needs it ([ADR-009](../decisions/ADR-009-selective-cqrs.md)) |
| Tenant and merchant naming conventions, including whether `TenantMerchant` is the right project name. This is the only open tenancy item; the tenancy model itself is accepted (section 7) | Tenant & Merchant design phase |
| Detailed per-service database schemas | Each service's design phase |
| SQL Server 2022 versus SQL Server 2025 | Before the first production-like deployment |
| Detailed observability implementation | Observability phase |
| Detailed Kubernetes deployment architecture | Deployment phase (advanced scope) |
| Saga state machine, timeouts and reservation expiration design | Checkout design phase |

**Resolved since the Phase 1 audit:**

- Identity protocol and provider: OAuth 2.0 / OpenID Connect with ASP.NET Core Identity and OpenIddict ([ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md), Phase 2.1).
