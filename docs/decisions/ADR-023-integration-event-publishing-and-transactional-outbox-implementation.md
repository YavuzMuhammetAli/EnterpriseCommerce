# ADR-023: Integration Event Publishing and the Transactional Outbox Implementation

- **Status:** Accepted (2026-11-23, by the project owner, after the design review and its final clarification of fact identity, version handling and replay; the preferred version per consumer and `publishedVersions` were added on the same day, before the ADR's first commit). Implementation: Phase 3 S4 steps 1 to 6, **done 2026-11-23** ([evidence](../architecture/PHASE_3_S4_EVENT_FLOW.md#10-test-evidence), [operations](../operations/TENANT_MERCHANT_EVENT_RELAY.md)); consumer rules (section 7) with the first consumer.
- **Date:** 2026-11-23
- **Phase:** 3 (Tenant & Merchant), slice S4, the first event-flow phase
- **Resolves:** the items that [ADR-005](ADR-005-kafka-for-asynchronous-communication.md) and [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md) section 5 defer to "the phase that introduces the first event flow":
  - the Kafka client library
  - topic naming and partitioning
  - the schema format
  - the outbox implementation (table, relay, cleanup, ordering)
  - an inbox or deduplication store

  It also resolves the PROJECT_DECISIONS section 13 rows "Kafka client library" and "Outbox implementation details".
- **Amends:** nothing in the decision text of ADR-005 or ADR-012. Their status lines point here.
- **Amended by:** [ADR-028](ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) (2026-11-24, Phase 5). For the Inventory producer only, the section 2 ordering guarantee is per aggregate in commit order, not per tenant. ADR-028 also applies section 8: the outbox code now lives in `EnterpriseCommerce.BuildingBlocks.Messaging`.
- **Related:**
  - [ADR-003](ADR-003-database-per-service.md) (database per service)
  - [ADR-009](ADR-009-selective-cqrs.md) (no mediator without a decision)
  - [ADR-014](ADR-014-logical-tenant-isolation.md) (tenant context in messages)
  - [ADR-018](ADR-018-account-security-tokens-and-security-email-delivery.md) (E3, deferred until the outbox design is approved)
  - [ADR-022](ADR-022-self-service-tenant-onboarding.md) (the two tenant creation paths)
- **Design and evidence:** [PHASE_3_S4_EVENT_FLOW.md](../architecture/PHASE_3_S4_EVENT_FLOW.md). **Contracts:** [docs/events](../events/README.md).

## Context

ADR-005 chose Kafka (KRaft) and ADR-012 chose the transactional outbox. Both deliberately left the implementation to the first event flow. ADR-012 section 3 also fixed the requirements that every message must meet:

- idempotent consumers, because delivery is at least once
- retry with backoff, and dead-letter handling
- correlation and causation identifiers on every message
- versioned schemas documented under `docs/events/`
- no sensitive data
- explicit tenant context where relevant

**Nothing more was recorded.** The Phase 0.1 event catalog is not in the repository (PHASE_3_TENANT_MERCHANT.md section 1), so there was no topic, no envelope field list and no schema convention to follow. `docs/events` defined no topic, and `tenant.events` did not exist.

Tenant & Merchant (S1 to S3) owns the first business fact that other services need: a tenant exists. It is created on two paths:

- trusted provisioning (`POST /internal/v1/tenants`)
- self-service onboarding (`POST /api/v1/tenants`, ADR-022)

Both commit in one SQL Server transaction. S1 to S3 published no event, and an architecture test (`MessagingDeferralTests`) forbade messaging packages and outbox types.

Constraints that shape the decision:

- **Layering.** Domain and Application never reference Kafka, EF Core or ASP.NET Core (ADR-002, `tests/ArchitectureTests`). Kafka code lives only in Infrastructure (ADR-005).
- **No shared contracts.** A service never references another service's projects. BuildingBlocks never holds shared DTOs that couple service contracts (SOLUTION_STRUCTURE.md).
- **No unnecessary abstractions.** No mediator without a decision (ADR-009), and BuildingBlocks code only once two services need it.
- **Local resources.** The Docker VM has about 3.8 GB, and SQL Server (2560 MB) and Kafka (1 GB) already take about 3.5 GB of it.
- **No exactly-once.** Nothing spans the SQL Server transaction and the Kafka write. Publication is at least once.

## Decision

### 1. Kafka client: Confluent.Kafka, in Infrastructure only

- The official Confluent .NET client (Apache-2.0, a wrapper of librdkafka) is the only Kafka library.
- Its latest stable 2.x release is pinned in `Directory.Packages.props` when it is first added, after checking .NET 10 support, the licence and the native assets.
- It is referenced only by Infrastructure projects, today `EnterpriseCommerce.TenantMerchant.Infrastructure`.
- No messaging framework is used (MassTransit, KafkaFlow, NServiceBus, Rebus, Wolverine, CAP), and no Schema Registry client.
- The architecture tests enforce all of this.

### 2. Topics, keys and consumer groups

- **One topic per producing service for its integration events: `<service>.events`.** The first is `tenant-merchant.events`.
  - Its event types share the topic.
  - The topic follows the owning service, not a domain noun, because a service owns several aggregates (tenant, membership, invitation, subscription).
- **Commands**, when they arrive (ADR-012 section 2), go to `<recipient-service>.commands`, owned by their recipient.
- **Characters:** lowercase letters, digits, `.` and `-`. Never `_`.
- **Partition key:** the tenant ID (lowercase GUID, `D` format) for every tenant-scoped message.
  - **The guarantee is per tenant, in commit order, within one partition.** There is no order across tenants.
  - It rests on two properties:
    - the producing service serializes the writes of one tenant (Tenant & Merchant does, ADR-021 and ADR-022)
    - the relay publishes sequentially (section 5)
- **Partitions, replication and retention:**
  - Locally and in CI: 3 partitions, replication factor 1, `cleanup.policy=delete`, and `retention.ms` of 7 days.
  - Production: replication factor 3 and `min.insync.replicas=2`. The partition count (from measured consumer parallelism) and the retention are set in the deployment phase, before any production data exists, because changing the partition count later remaps keys.
  - Compaction is not used for event topics.
- **Consumer groups:** `<consumer-service>.<purpose>`, for example `catalog.tenant-projection`, owned by the consumer service.
- **Dead-letter topics:** `<consumer-group>.dlt`.
- **Topics are created explicitly (ADR-005), never by an application.**
  - They are declared in a committed manifest and created by the Compose setup locally, by the test fixtures for tests, and by infrastructure as code in deployed environments.
  - An application checks at startup that its topic exists and reports it if not.

### 3. Serialization and schema evolution: JSON with committed JSON Schema, no registry

- **Encoding.** Messages are UTF-8 JSON (System.Text.Json):
  - property names in camelCase
  - timestamps in ISO-8601 UTC with `Z`
  - GUIDs lowercase in `D` format
  - enums as strings
  - an envelope of at most 64 KiB
- **Schemas.** Every event version has a committed JSON Schema (draft 2020-12) and a golden sample under `docs/events/schemas/<producer>/`. The envelope has its own schema.
- **Two schemas, two validation steps.**
  - The shared envelope schema validates every message and requires only that `payload` is an object.
  - The payload is validated by the schema of (`producer`, `eventType`, `eventVersion`).
  - `eventVersion` versions the payload only. The envelope is versioned independently and changes only additively.
  - Every schema sets `additionalProperties: true`, so that an optional property added later never invalidates a message for a consumer that validates against an older copy of the schema.
  - Each open-set property has a bounded token rule (`^[A-Z][A-Za-z0-9]{0,31}$`), so an unknown value is still a schema-valid, safely loggable value.
- **Evolution rules:**
  1. `eventVersion` is an integer major version. Within a version, only **optional** properties may be added. Removing, renaming or retyping a property, making it required, or changing its meaning needs a new version.
  2. Consumers are tolerant readers: they ignore unknown properties. Properties documented as *open sets* may gain values, and consumers must handle unknown values.
  3. A breaking change publishes `vN+1` next to `vN` on the same topic until the documented consumers have migrated. `vN` is then retired with a changelog entry.
     - **Dual publication:** both versions of one fact are written in the same transaction with the **same `eventId`**, and (`eventId`, `eventVersion`) is unique.
     - The two messages state one fact and both carry `publishedVersions: [N, N+1]`. **Every version of the same fact carries the identical `publishedVersions` list**, computed once per fact by the producer. Their order within the partition is not part of the contract.
     - A consumer applies the fact from **one selected version**, chosen from its declared preferred version and `publishedVersions`, never from arrival order (section 7).
     - Every other version is acknowledged without a processed-event record.
  4. The envelope changes only additively. A breaking envelope change needs a new topic.
- **CI enforcement**, test-only and with no extra package:
  - **Schema snapshot:** the schema of each contract type, generated with the in-box `System.Text.Json.Schema.JsonSchemaExporter`, must match the committed schema after normalization.
  - **Golden samples:** the samples of every version ever published must still deserialize into the current contract without data loss.
  - **Producer conformance:** the real mapper's output for a real aggregate has the shape of the sample.
- **Consumers write their own types** from the published schema. No contract assembly is shared between services.

### 4. The envelope

The body is the authoritative, self-contained envelope. The same bytes are stored in the outbox row and published.

| Field | Type | Rule |
|---|---|---|
| `eventId` | GUID (version 7) | **The identity of the logical business fact**, not of an individual serialized message. The consumers' deduplication key. Every message stating the same fact carries it: redeliveries, and the versions of a dual publication. A message is identified by (`eventId`, `eventVersion`). |
| `eventType` | string | Past-tense PascalCase name, unique within its producer, for example `TenantCreated` |
| `eventVersion` | integer | **The version of the payload contract** of (`producer`, `eventType`), its major version. It versions neither the envelope nor the aggregate's state. |
| `publishedVersions` | array of integers | **The versions in which the producer publishes this fact**, identical in every message of the fact and containing `eventVersion`: `[1]` normally, `[N, N+1]` during a dual publication. It lets a consumer select one version per fact deterministically (section 7). |
| `producer` | string | The producing service, for example `tenant-merchant`. (`producer`, `eventType`, `eventVersion`) identifies the schema. |
| `aggregateId` | GUID | The aggregate the fact is about |
| `tenantId` | GUID or `null` | Required for every tenant-scoped message. Consumers validate it before use (ADR-014 rule 2). |
| `occurredAtUtc` | timestamp | The business time of the fact, not the publish time |
| `correlationId` | string, 1 to 64 characters of `A-Z a-z 0-9 - _ . :` | The flow's correlation ID, from `X-Correlation-ID` for HTTP-initiated work |
| `causationId` | string, same rules | **Required.** The `eventId` of the message that caused this one. For a message caused directly by an HTTP request, that request's correlation ID: a root message has `causationId == correlationId`. |
| `payload` | object | The event-specific data, under its own versioned schema |

- **Kafka headers** copy routing and tracing values, so consumers can filter without parsing the body:
  - `event-id`, `event-type`, `event-version` and `correlation-id`
  - `content-type: application/json`

  If a header and the body disagree, the body is authoritative and the message is malformed.
- W3C trace context headers are added in the observability phase.
- **Never in a message:**
  - credentials, tokens or secrets
  - payment instrument data
  - email addresses
  - idempotency keys and request hashes
  - client IDs and actor references
  - Identity subject IDs, unless an accepted contract explicitly needs them (membership events will decide this)

### 5. The transactional outbox

**Table.** Each publishing service owns an `OutboxMessages` table in its own database, created by a migration and without foreign keys to business tables:

| Column | Purpose |
|---|---|
| `Sequence` | `bigint IDENTITY`, the clustered primary key and the publication order |
| `EventId` | Equals the envelope's `eventId`. (`EventId`, `EventVersion`) is unique, which allows dual publication (section 3). |
| `Topic` | Fixed when the row is written |
| `MessageKey` | The tenant ID |
| `EventType`, `EventVersion` | Diagnostics |
| `Envelope` | The exact JSON that is published, at most 64 KiB |
| `OccurredAt`, `PublishedAt` | `PublishedAt` is `NULL` while the row is pending |
| `Attempts`, `LastAttemptAt`, `LastError` | `LastError` holds the error kind and code only, never the payload |

Filtered indexes serve the pending poll (`PublishedAt IS NULL`) and the cleanup (`PublishedAt IS NOT NULL`).

**Writing: from domain event to outbox row, inside the business transaction.**

- **Domain.** An aggregate records internal domain events in a minimal list (`IHasDomainEvents`, `IDomainEvent`), defined in the service's own Domain project. There is no base class, no dispatcher and no mediator.
- **Infrastructure.** The service's `DbContext.SaveChangesAsync` does the following, before calling the base implementation:
  1. collects the pending domain events of the tracked aggregates
  2. translates the *public* ones through one explicit mapper into integration events
  3. serializes their envelopes
  4. adds the outbox rows

  The rows therefore commit in the same transaction as the state change, or not at all. Domain events and integration events stay distinct types.
- **The transaction of a tenant creation** contains five rows:
  - the tenant
  - its merchant profile
  - its initial `MerchantOwner` membership (the active-owner trigger runs inside it)
  - the membership's audit record
  - the `TenantCreated` outbox row

  It is the single `SaveChanges` of trusted provisioning, or the explicit transaction of self-service onboarding. An integration event therefore states the **complete, committed** outcome of its business operation, never an intermediate step.
- **Kafka is never called inside a business transaction.** The request returns after the commit without waiting for Kafka. Publication is asynchronous, by the relay, after the commit.
- **Message metadata** (correlation and causation) is not domain data. Application code passes it explicitly to the write store or session. Saving an aggregate with public domain events and no metadata fails closed.
- **No business code publishes to Kafka.**

**Publishing: the relay.** A hosted service of the owning service:

- **Leader election.** One relay is active per service. It holds a session-owned SQL Server application lock (`sp_getapplock`, `@LockOwner = 'Session'`, no wait) on a dedicated connection. Other instances stand by and retry periodically. A broken leader connection stops publishing at once.
- **Polling.**
  - The relay polls every second while idle and drains at once while batches come back full.
  - A batch is up to 100 pending rows in `Sequence` order, read with `READPAST`, so an uncommitted row is taken by a later poll.
- **The producer** uses `acks=all`, `enable.idempotence=true` and a bounded delivery timeout.
- **Sequential publication.** The relay awaits each delivery report before it sends the next message. A failure stops the batch, so nothing after a failed row is sent. After the batch, the delivered prefix is marked `PublishedAt` in one statement.
- **Cleanup.** The leader deletes published rows older than a retention period (7 days by default) in bounded batches. Pending rows are never deleted.
- **Graceful shutdown.** On stop, the relay:
  1. takes no new batch
  2. awaits the delivery in flight within the host's shutdown timeout
  3. marks what was delivered
  4. disposes the producer
  5. releases the lock

### 6. Guarantees and failure policy

- **Delivery is at least once. Exactly-once is not claimed.** A row is republished after:
  - a crash between delivery and marking
  - a leader handover while the old leader is still publishing
  - a restart

  A duplicate carries the same `eventId` and the same bytes.
- **Kafka unavailable.** Business requests are unaffected and rows accumulate. The relay records the attempt on the head row and backs off exponentially with jitter (1 second, capped at 60 seconds). Once Kafka is back, the backlog drains in order.
- **No automatic skipping on the producer side.** A row that keeps failing (a missing topic, missing authorization or misconfiguration) halts the relay at that row: skipping would break per-tenant order and lose a committed fact. Instead:
  - the failure is prevented at write time: the payload is serialized in the transaction, the size is checked, and the topic is fixed
  - the halt is logged and visible in health and metrics
  - a documented operator procedure covers it

  **This is how ADR-012's dead-letter requirement applies to the producer side.** Dead-letter topics are a consumer-side mechanism (section 7).
- **Health.** An `outbox` health check reports `Degraded` when the oldest pending row is older than a threshold (5 minutes by default) or when the relay is backing off.
  - It is never part of readiness, so a Kafka outage never takes the API out of rotation.
  - Meters report the pending count, the oldest pending age, publications, failures and the leader status.
- **Configuration.** Outside Development, a service that publishes refuses to start without Kafka settings, or with an unencrypted, unauthenticated connection. In Development without Kafka settings, the relay is disabled with a warning.

### 7. Consumer rules

These rules apply to every consumer. They are implemented with the first real consumer, whose slice must include the consumer acceptance tests of [PHASE_3_S4_EVENT_FLOW.md section 8](../architecture/PHASE_3_S4_EVENT_FLOW.md#8-test-plan) (version support, duplicates, and replay after an upgrade).

- **Offsets** are stored and committed only after the message is finished: applied, skipped as a duplicate, unsupported, or dead-lettered.
- **The offset commit and the processed-event record are different things.**
  - The offset commit is the group's position in Kafka.
  - The processed-event record is a row of the consumer's own database (the "inbox", primary key (`Consumer`, `EventId`), where `Consumer` is the consumer group). It states that the consumer applied the logical fact.
  - The record is written **only** when a supported version of the fact is applied, and in the same transaction as the effect. Its `EventVersion` column is diagnostic and never part of the key.
- **Version selection: one preferred version per consumer and event type.**
  - A consumer declares the versions it supports, `S`, and exactly one preferred version `p` in `S`. This is a deployment setting.
  - For a message with `publishedVersions` `P`, the fact's **selected version** is `p` if `p` is in `P`, otherwise the highest version in both `S` and `P` (the fallback for facts published only in a non-preferred version). If no version is common to `S` and `P`, the fact is unsupported.
  - **Only the selected version is processed**, and it creates the processed-event record (`consumer group`, `eventId`).
  - A supported but not-selected version is acknowledged and logged **without a record**, whether it arrives before or after the selected version.
  - A message whose `eventVersion` is not in its `publishedVersions` is malformed and goes to the dead-letter topic.
  - The result depends only on (`S`, `p`, `P`), so **a fact is applied from the same version in every arrival order**:
    - when the preferred version arrives first, the later version is skipped (and its record would deduplicate it)
    - when a non-preferred version arrives first, it is skipped, and the preferred version is applied later
- **Deduplication per logical fact.** A message whose `eventId` already has a record (an earlier delivery, or an application under an earlier selection) is rolled back without effect and acknowledged.
  - The unique key makes this hold under concurrent deliveries too.
  - **A logical fact is therefore applied at most once per consumer, even when it is delivered repeatedly or published in several versions.**
  - Together with at-least-once delivery, it is applied once while its record is retained. This is not exactly-once delivery.
  - Effects outside the consumer's transaction need their own idempotency (for example the `eventId` as an idempotency key).
- **Order:** each partition is processed sequentially.
- **Transient failures** are retried with bounded backoff, blocking the partition.
- **Malformed messages, or messages that keep failing,** go to the consumer group's dead-letter topic with the failure reason.
- **An unsupported `eventType` or `eventVersion`** is acknowledged (its offset committed) and logged. It **never creates a processed-event record**, so it never suppresses another version of the fact, and it stays replayable. It is never a failure.
- **Replay after a consumer upgrade.**
  - A consumer that gains support for a version or event type resets its group's offsets (stopped group, `kafka-consumer-groups.sh --reset-offsets`) to the start of the gap, within topic retention.
  - Facts it acknowledged without a record are then selected under the new declaration and applied. Facts it already applied, from whichever version was selected then, are skipped by their records.
- **Changing the preferred version** affects only facts not yet applied.
  - Applied facts keep their records and their data from the version selected at the time. A replay deduplicates their newly selected version.
  - Every preference change is followed by a gap replay from before the switch point. Otherwise a fact whose first-arriving version was skipped under the old preference, and whose other version is not selected under the new one, would be applied from neither.
  - **Replay semantics.** A gap replay recovers facts that were previously skipped and not recorded. It never upgrades facts already applied and recorded. A projection that must be fully migrated to a newly preferred version requires a **rebuild**: delete the projection and its processed-event records atomically, in one transaction, then replay from the earliest retained offset.
  - **Processed-event records are retained at least as long as the topic's retention plus the longest planned replay window**, and cleaned up by age, never by version. A record deleted earlier would let a replay apply its fact again.
  - Replayed facts are applied after later facts. When the consumer's effects depend on that order, it **rebuilds** instead: in one transaction it deletes its projection and the projection's records, then replays from the earliest retained offset.
  - The procedure and its implications are in [docs/events/README.md](../events/README.md#replay-after-a-consumer-upgrade).
- **An unknown value of an open-set property makes a valid message.** The consumer:
  - never maps it to a known value, assumes a default, or drops it
  - processes the message with the raw value preserved, and **fails closed**: no capability that needs a known state, such as treating a tenant as `Active`
  - logs a Warning with the event ID, the property and the value, and counts it in a metric tagged by property
  - does not retry it (the outcome is deterministic) and does not dead-letter it (that would lose a valid fact). An upgraded consumer re-evaluates the stored raw values without a replay.
- **Dead-letter topics are only for messages that fail the envelope or payload schema, or whose headers disagree with the body.** Such a message is a producer defect.
- **`tenantId`** is validated, and every effect is scoped to it.
- **New consumers bootstrap** through a designed replay or backfill, never by assuming unlimited retention.

### 8. Code location

- The outbox, envelope and relay code lives in the publishing service's Infrastructure (`EnterpriseCommerce.TenantMerchant.Infrastructure/Messaging/`), with no dependency on that service's business types outside one mapper.
- It moves to `EnterpriseCommerce.BuildingBlocks.Messaging` when a second service publishes, per the BuildingBlocks policy. Likely candidates: Identity for E3, or Catalog, whose MongoDB outbox (ADR-012) will show the right store abstraction.

### 9. The first event

- **`TenantCreated` v1 on `tenant-merchant.events`,** emitted once by both creation paths, with `origin` set to `Provisioned` or `SelfService`. This name replaces the provisional `TenantProvisioned`: that name collides with the `Provisioned` origin and would not fit a later pending tenant.
- **Semantics.** It states the complete, committed creation: the tenant, its merchant profile, the initial owner membership and its audit record (section 5). Replays and refused requests emit nothing.
- **Payload:**
  - `slug`
  - `displayName`, the public merchant name, exactly as stored (`MerchantDisplayName`)
  - `status`, an open set
  - `origin`, an open set and informational only

  The payload schema states the creation validation rules: the slug pattern, a display name trimmed of .NET white space with no control characters (so never white space only), and the token rule of the open sets.
- **No subject, client, key or actor values. The initial owner's identity is intentionally excluded:**
  - no consumer of S4 needs it
  - membership authorization needs current memberships, not a creation-time snapshot that goes stale
  - a subject ID is pseudonymous personal data that would stay in the topic beyond the service's control
- **Membership events are deferred** to the slice of their first consumer. They arrive as their own contract, with the complete lifecycle set; the owner membership's creation event is emitted in the same transaction as `TenantCreated` and ordered after it. A partial set of membership events would let a read model authorize a suspended member. Contract: [TENANT_MERCHANT_EVENTS.md](../events/TENANT_MERCHANT_EVENTS.md).

## Consequences

- **Positive:**
  - No lost or phantom messages between the database write and the publication.
  - The API stays available while Kafka is down.
  - Per-tenant order holds across all event types of a service.
  - Domain and Application stay free of messaging code.
  - Every committed tenant creation emits its event structurally, so a future creation path cannot forget it.
- **Positive:**
  - Contracts are readable JSON, checked in CI without new infrastructure.
  - Other services depend on documented schemas, never on another service's assemblies.
- **Negative:**
  - At-least-once delivery makes consumer deduplication mandatory.
  - Sequential publication by a single leader limits throughput. That is enough for Tenant & Merchant, and a high-volume service will need per-partition pipelining, with its own evidence.
- **Negative:**
  - A poison row halts publication until an operator acts. The design makes such a row unlikely, but not impossible.
  - Halting is preferred to losing order or facts. It requires monitoring of the `Degraded` state.
- **Negative:**
  - Schema compatibility is enforced by tests and review, not by a broker-side registry.
  - Every service writes its own consumer types, and its own consumer loop (about 200 lines) until extraction.
- **Neutral:** some publication latency (polling, about one second), and a new table per publishing service.

## Alternatives considered

- **Kafka client:**
  - **MassTransit:** version 9 is commercial and the open-source version 8 support window ends around the end of 2026. It needs a primary bus transport besides the Kafka rider and brings its own outbox, envelope and mediator-like pipeline (ADR-009). Rejected.
  - **KafkaFlow:** a middleware layer over Confluent.Kafka, from a smaller, single-sponsor community, with no need shown for it. Rejected for now.
  - **DotNetCore.CAP:** a built-in SQL Server outbox, but it owns the tables, envelope and retry semantics. Rejected.
  - **Wolverine:** a full framework with a mediator. Rejected.
- **Topics:**
  - **One topic per event type:** it loses per-tenant ordering across event types, and topics multiply. Rejected.
  - **Domain-named topics** (`tenant.events`): ambiguous for a service with several aggregates. Rejected.
- **Avro or Protobuf with a Schema Registry:**
  - Confluent Schema Registry is under the Confluent Community License. Apicurio and Karapace are Apache-2.0 alternatives.
  - It adds a JVM container and makes the registry a runtime dependency of producers and consumers.
  - Binary payloads and code generation make the developer experience worse.
  - Its benefit, enforcement at the broker, is not needed with one producer and no consumer.

  Rejected for now. **Revisit** with many producing teams, external consumers, or measured volume where the JSON size matters.
- **CloudEvents with its SDK:** a standard envelope, but its extension attributes cannot use our camelCase names, and the SDK adds dependencies. Its attributes can be mapped later. Not adopted.
- **Envelope in headers only (binary mode):** the outbox row and the dead-letter topics would lose a self-contained, inspectable message. Rejected.
- **Application code enqueuing integration events explicitly:** every new write path would have to remember it. Rejected in favour of mapping domain events at `SaveChanges`.
- **A generic EF Core interceptor or a base aggregate class:** an abstraction without a second user. Rejected.
- **Several relays claiming rows (`UPDLOCK, READPAST`):** this breaks per-key order unless claims are per partition. That is complexity without a measured need. Rejected.
- **Pipelined (concurrent) publication:** after a failure, a later row of the same key may be delivered before an earlier one is republished. Rejected for now.
- **Kafka transactions or a transactional producer:** they cannot include the database marking, so they still give at-least-once. Rejected.
- **The first-arriving supported version wins:** the applied version depends on arrival order, which the contract does not define. Not deterministic. Rejected.
- **A preferred version without `publishedVersions`:** a consumer cannot tell from a non-preferred message whether the preferred one will follow. It would either never apply facts published only in the non-preferred version, or depend on arrival order. Rejected.
- **Selecting versions by a documented dual-publication time window:** imprecise across partitions and replays, and it relies on clocks. Rejected.
- **Change Data Capture (Debezium):** a valid relay, but it adds Kafka Connect infrastructure. Rejected for now (ADR-012 allowed it as an option).
- **A producer-side dead-letter topic, or parking failed rows and continuing:** it breaks per-tenant order and hides lost facts. Rejected.
- **Membership events in the first slice:** no consumer exists, the shared membership data is an open design item (PROJECT_DECISIONS section 7), and a partial set is unsafe. Deferred.
- **`BuildingBlocks.Messaging` now:** only one publisher exists, and the store abstraction for Catalog's MongoDB outbox is unknown. Deferred until a second publisher exists.
