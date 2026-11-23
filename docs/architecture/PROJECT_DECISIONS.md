# Project Decisions

**This is the canonical, consolidated reference** for the architectural and technical decisions of EnterpriseCommerce. It combines the Phase 0 architecture agreement, the design work between Phase 0 and Phase 1, and the Phase 1 ADRs.

How this document relates to the others:

- **Architecture Decision Records** ([docs/decisions](../decisions/README.md)) are the historical records of individual decisions, including their alternatives. This document summarizes the *current* outcome and links to them.
- **[CLAUDE.md](../../CLAUDE.md)** is the operational instruction manual for implementation sessions.
- **[SERVICE_CATALOG.md](SERVICE_CATALOG.md)** describes service responsibilities in more detail.

**Change policy:** a decision in this document changes only through a new ADR that supersedes or amends the old one. This document is then updated in the same change. If this document and an ADR disagree, stop and resolve the inconsistency. Never guess.

Last consolidated: 2026-11-17 (Phase 1 final audit). Updated 2026-11-17 for Phase 2.1 (Identity provider selection, [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)). Updated 2026-11-18 for Phase 2.2 (browser client architecture, token and session strategy, account security tokens: [ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md), [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md), [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)). Updated 2026-11-20 for Phase 2.3 slice S6 (signing and encryption key rollover and retirement, [ADR-019](../decisions/ADR-019-signing-and-encryption-key-rollover-and-retirement.md)). Updated 2026-11-21 for Phase 3 slice S1 (Tenant & Merchant naming and tenant-merchant cardinality, [ADR-020](../decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md)), slice S2 (merchant invitations and membership administration, [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md)) and slice S3 (self-service tenant onboarding, [ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md)). Updated 2026-11-23 for Phase 3 slice S4: the integration event publishing and outbox implementation design ([ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md), accepted) resolves two section 13 rows, the Kafka client library and the outbox implementation details.

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
| ORM | Entity Framework Core | In use (Identity persistence and migrations, Phase 2.3; Tenant & Merchant, Phase 3 S1 to S3) |
| Relational database | SQL Server | In use (Identity and Tenant & Merchant databases, Phase 2.3 and Phase 3 S1; local container) |
| Document database | MongoDB | Local container available |
| Cache | Redis | Local container available. A separate, dedicated `noeviction` Redis is in use as the BFF session store (Phase 2.3 S7, ADR-016), not as a cache. |
| Messaging | Apache Kafka (KRaft), client library Confluent.Kafka in Infrastructure only, JSON with committed JSON Schema (no Schema Registry), transactional outbox ([ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md)) | Local container available. ADR-023 accepted. Phase 3 S4 step 1: Confluent.Kafka 2.16.0 pinned and referenced by TenantMerchant.Infrastructure only; no messaging code yet. |
| API Gateway | YARP | In use |
| Identity provider | ASP.NET Core Identity (user store) with OpenIddict 7.9.0 or later (OAuth 2.0 / OpenID Connect server), [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md), [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) | Accepted; implemented in Phase 2.3 (slices S1 to S7, complete) and tested locally; not production-verified |
| Browser clients | Backend-for-frontend for every front end: one ASP.NET Core codebase deployed once per front end, behind the Gateway, [ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md) | Accepted; implemented in Phase 2.3 S7 (`src/Bff`) and tested locally; the real Safari gate is open and it is not production-verified |
| Containers | Docker | In use |
| Local orchestration | Docker Compose (profiles) | In use |
| Logging | Serilog | In use |
| Telemetry | OpenTelemetry | Planned (observability phase) |
| Metrics | Prometheus | Planned (observability phase) |
| Dashboards | Grafana | Planned (observability phase) |
| Log aggregation | Loki | Planned (observability phase) |
| Testing | xUnit (v3) | In use |
| CI/CD | GitHub Actions | In use: runs on every push and pull request to `main` (build and test with SQL Server 2022 and the session Redis, Playwright browser tests, Docker Compose validation). The runs up to the Phase 3 S3 commit `f67804f` (2026-11-21) succeeded. **Until 2026-11-23 the test steps could not fail on a test failure** (no `pipefail`; [CI-1](../testing/README.md#ci-1-test-steps-could-not-fail-on-a-test-failure-fixed-2026-11-23)), so those runs verify restore, build and the publish and secret checks, not the test results. Commits that are not pushed are not CI-verified. |
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
| Transactional Outbox (direction, and its implementation design) | [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md), [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) |
| Sagas: orchestration or choreography where appropriate | [ADR-013](../decisions/ADR-013-saga-based-workflow-coordination.md) |
| Database per service | [ADR-003](../decisions/ADR-003-database-per-service.md) |
| API Gateway using YARP | [ADR-007](../decisions/ADR-007-yarp-api-gateway.md) |
| Event-driven communication through Kafka | [ADR-005](../decisions/ADR-005-kafka-for-asynchronous-communication.md), [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md), [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) |
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
- BFF session tickets live in a **dedicated Redis instance** with `maxmemory-policy noeviction`, never shared with cache workloads. Losing it signs users out and corrupts no business data ([ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md)).
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
- **Naming and cardinality (resolved, [ADR-020](../decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md)):**
  - The service is `TenantMerchant` (projects `EnterpriseCommerce.TenantMerchant.*`, audience `tenant-merchant-api`, scopes `svc.tenant-merchant.*`).
  - **A tenant is one merchant organization.** Memberships and merchant roles are tenant-scoped, and other services scope merchant-owned data by the tenant ID.
- **Invitations and membership administration (accepted, [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md)):**
  - An invitation's single-use token is stored only as a hash and returned once to the inviter until Notification delivers it.
  - Acceptance also requires Identity's verdict that the caller's verified email is the invited address (D14), through a narrow internal endpoint; access tokens still carry no email.
  - Every membership change is serialized per tenant (an update lock on the tenant row) and commits with an append-only audit record. A tenant can never be left without an active owner, and a trigger backs this up.
- **Self-service onboarding (accepted, [ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md)):**
  - A merchant-portal user creates a tenant and becomes its owner, active at once. Merchant applications and review wait for Admin.
  - One tenant per user by default (configurable), and idempotency keys scoped per user, both serialized per user.
  - An owner membership is created only together with its new tenant (amends ADR-021 section 4).
- **Design-level details within the accepted model** are worked out in later phases and must conform to ADR-014. They are not open architectural decisions:
  - the tenant context claim shape: resolved by [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md). Access tokens carry no tenant claim, and the tenant is an explicit request parameter validated by the owning service
  - the membership data shared with other services (Tenant & Merchant design; it needs the first event flow, [PHASE_3_TENANT_MERCHANT.md section 7](PHASE_3_TENANT_MERCHANT.md#7-integration-events))
  - the merchant role catalog and the membership rules: `MerchantOwner`, `MerchantAdmin` and `MerchantStaff`, defined in [PHASE_3_TENANT_MERCHANT.md section 3](PHASE_3_TENANT_MERCHANT.md#3-merchant-roles-permissions-and-membership-rules), with the S2 permission matrix in [PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md section 3](PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md#3-role-and-permission-matrix)
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

Governing ADRs: [ADR-005](../decisions/ADR-005-kafka-for-asynchronous-communication.md), [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md), [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md). Conventions and contracts: [docs/events](../events/README.md).

- **Apache Kafka in KRaft mode** (no ZooKeeper) is the messaging platform.
- **REST** is used for synchronous interactions. **Kafka** is used for asynchronous integration events and commands.
- **Domain events** stay internal to their owning service. **Integration events** are the cross-service contracts.
- Event schemas are **versioned**. Consumers are **idempotent**.
- Retry, backoff, dead-letter handling and correlation identifiers are required considerations for every flow.
- **Transactional Outbox** is the accepted direction (ADR-012), and its implementation design is accepted (ADR-023). Outbox code follows ADR-023 only.
- **Client library (ADR-023):** Confluent.Kafka, referenced only by Infrastructure projects. No messaging framework and no Schema Registry.
- **Topics (ADR-023):**
  - One topic per producing service, `<service>.events`. Commands go to `<recipient>.commands`.
  - The key is the tenant ID, so the order is per tenant, in commit order.
  - Topics are created explicitly and never by an application.
  - Consumer groups are named `<consumer-service>.<purpose>`, with dead-letter topics `<group>.dlt`.
- **Messages (ADR-023):**
  - UTF-8 JSON with committed JSON Schema files, and a self-contained envelope.
  - `eventId` is the identity of the logical fact and `eventVersion` the version of the payload contract.
  - `publishedVersions` lists the versions in which the fact is published.
  - Only optional additions within a version. A breaking change dual-publishes a new version with the same `eventId`.
- **Outbox (ADR-023):**
  - Outbox rows are written in the business transaction, mapped from domain events at `SaveChanges`.
  - One leader relay per service publishes sequentially after the commit.
  - Delivery is at least once and never exactly once. A row that cannot be published halts the relay; it is never skipped.
- **Consumers (ADR-023):**
  - A processed-event record per (consumer, `eventId`), written with the effect, so a logical fact is applied at most once per consumer.
  - Each consumer declares one preferred version per event type, and applies a fact only from its selected version: the preferred version if it is published for that fact, otherwise the highest other supported one. The result never depends on arrival order.
  - Unsupported and not-selected versions are acknowledged without a record and stay replayable.
  - A preference change is followed by a gap replay or a rebuild.
  - Unknown open-set values fail closed. Dead-letter topics are only for schema-invalid messages.
- **First event:** `TenantCreated` v1 on `tenant-merchant.events` ([TENANT_MERCHANT_EVENTS.md](../events/TENANT_MERCHANT_EVENTS.md)). Membership events are deferred to the slice of their first consumer.
- Sensitive information is never published in integration events. Identity subject IDs are not published unless an accepted contract needs them.

## 10. Security

- **Identity provider and protocol (accepted, [ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)):**
  - The Identity service is the platform's **OAuth 2.0 / OpenID Connect** authorization server, built with **OpenIddict** and **ASP.NET Core Identity**, persisted in Identity's own SQL Server database.
  - Marketplace authorization is never delegated to OpenIddict.
  - ASP.NET Core Identity roles are **not authoritative** for platform roles (Admin) or merchant roles (Tenant & Merchant).
  - The Resource Owner Password Credentials grant and the implicit flow are **rejected**.
- **Browser clients (accepted, [ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md)):** every front end uses a backend-for-frontend behind the Gateway. Tokens never reach browser JavaScript. Authorization code + PKCE `S256`, `private_key_jwt` in deployed environments, server-side session tickets, layered CSRF defense, HTTPS everywhere. Real Safari behaviour is a verification gate before production-like deployment.
- **Tokens and sessions (accepted, [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md)):**
  - RS256-signed JWT access tokens with `typ: at+jwt` and minimal claims (including `ec_ptype`). No role or tenant claims.
  - One audience per front-end client for user tokens. Per-service audiences for service tokens. The `svc-` client ID prefix is reserved for service clients.
  - Services validate tokens **locally** with the standard ASP.NET Core JWT bearer handler (issuer, audience, algorithm, `typ`, 30-second skew). They don't call Identity on every request.
  - Rotated refresh tokens with no reuse leeway, a mandatory redemption ledger, and a presenter-checked reuse policy.
  - The per-user **security version** and the authorization status are the authoritative revocation state. Authoritative changes commit in one short transaction with a whole-transaction retry on deadlock. OpenIddict bulk revocation is post-commit cleanup.
  - Every credential entry happens on Identity-hosted pages.
- **Key rollover and retirement (accepted, [ADR-019](../decisions/ADR-019-signing-and-encryption-key-rollover-and-retirement.md)):**
  - Signing and encryption keys carry an explicit state in configuration: `Pending` (published, never signs), `Active` (exactly one per use) or `Retired` (with `RetiredAt`). Key IDs are SHA-256 thumbprints.
  - Refresh tokens and authorization codes are signed with the signing key too. A retired key is therefore kept until every token it signed or encrypted has expired, plus the 30-second skew. The strict minimum is the end of the activation rollout plus the longest such token lifetime (7 days with the accepted initial lifetimes of PHASE_2_2 section 6.4) plus 30 seconds.
  - The implemented rule is longer, deliberately: a retired signing key is kept for the replay protection window (the longest configured lifetime plus a safety margin) + 30 seconds + the validator cache allowance, and a retired encryption key for the window + 30 seconds. With those lifetimes that is 32 days 30 seconds and 31 days 30 seconds after `RetiredAt`.
  - A refresh-token rotation never extends a token of a retired key: its successor is issued with the Active keys and capped by the absolute session deadline.
  - `RetiredAt` is set when the activation rollout starts. With those lifetimes, a rollout may last at most 24 days when it rotates both keys, and 25 days for the signing key alone; the target is the retention safety margin. A longer rollout requires recalculating the removal date from the actual last issuance time (ADR-019 section 4.1).
  - Emergency rotation removes a compromised key at once. Early removal always fails closed.
- **Merchant invitation acceptance (accepted, [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md)):** the invitation token plus Identity's verified-email verdict, requested by the allow-listed service client `svc-tenant-merchant` with the scope `svc.identity.account-verification`. Identity returns a verdict, never an address. No verdict means no membership.
- **Account security tokens and email (accepted, [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)):** hashed, single-use confirmation and reset tokens consumed atomically with the account change. Notification owns production email delivery, and Identity hands emails over through a bounded in-process queue with status-only reconciliation.
- The Identity API contract is in [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md).
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
| Detailed Identity data model (within ADR-015, ADR-017 and ADR-018) | Phase 2.3 implementation |
| Concrete signing key store and Data Protection key store outside local development (the direction is in ADR-017 and ADR-019): a KMS or HSM provider, or an explicitly accepted certificate-file key-management procedure, including the rotation of the Data Protection key-encryption certificate | Deployment phase, before any production-like deployment |
| Retention periods for expired account security tokens and terminal security email delivery records (finding I35; they are not deleted until then) | Before the first production-like deployment |
| Per-service user access token audiences ([ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) section 3) | When a concrete need appears |
| Opaque-reference security email handoff (E3, [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)) | The outbox design it waited for is accepted (ADR-023). Decided when Identity's messaging work is scheduled; not part of Phase 3 S4. |
| Mediator library selection (if any) | When the first service needs it ([ADR-009](../decisions/ADR-009-selective-cqrs.md)) |
| Detailed per-service database schemas | Each service's design phase |
| SQL Server 2022 versus SQL Server 2025 | Before the first production-like deployment |
| Detailed observability implementation | Observability phase |
| Detailed Kubernetes deployment architecture | Deployment phase (advanced scope) |
| Saga state machine, timeouts and reservation expiration design | Checkout design phase |

**Resolved since the Phase 1 audit:**

- Identity protocol and provider: OAuth 2.0 / OpenID Connect with ASP.NET Core Identity and OpenIddict ([ADR-015](../decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md), Phase 2.1).
- Browser client architecture: a backend-for-frontend for every front end ([ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md), Phase 2.2).
- Token and session strategy, access token format, resource-server validation, and the direction for signing key storage and rotation ([ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md), Phase 2.2).
- Email confirmation and password reset token semantics, and security email delivery ([ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md), Phase 2.2).
- Signing and encryption key rollover and the retention of retired keys ([ADR-019](../decisions/ADR-019-signing-and-encryption-key-rollover-and-retirement.md), Phase 2.3).
- Tenant and merchant naming (`TenantMerchant`) and the tenant-merchant cardinality, one merchant per tenant ([ADR-020](../decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md), Phase 3 S1).
- Merchant invitations, the invitation email match (D14), per-tenant serialization of membership changes, and the membership audit trail ([ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md), Phase 3 S2).
- Self-service tenant onboarding: immediate activation, the per-user limit, per-user idempotency, and the owner rule for new tenants ([ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md), Phase 3 S3).
- Kafka client library and outbox implementation details, together with the other items ADR-012 section 5 deferred to the first event flow ([ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md), Phase 3 S4):
  - topic naming and partitioning
  - the schema format
  - the envelope
  - consumer deduplication and replay
