# Phase 3 S4: First Integration Event Flow and Transactional Outbox

Slice S4 of the Tenant & Merchant service ([PHASE_3_TENANT_MERCHANT.md](PHASE_3_TENANT_MERCHANT.md)): the platform's first integration event, published through a transactional outbox to Kafka.

- **Decisions:** [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) (**accepted** 2026-11-23).
- **Contracts:** [docs/events/README.md](../events/README.md) (conventions) and [TENANT_MERCHANT_EVENTS.md](../events/TENANT_MERCHANT_EVENTS.md) (`TenantCreated` v1).
- **Status:** **complete** (2026-11-23). Steps 0 to 6 are done: the decisions and contracts, the Kafka client package and the messaging rules, the domain events, the outbox, the relay with its producer, health and meters, the Kafka tests and CI, and this evidence ([section 10](#10-test-evidence)).
  - **Implemented and tested:** the producer side.
  - **Accepted but not implemented:** the consumer rules of ADR-023 section 7 and the consumer acceptance tests C1 to C6, which bind the first consumer's slice.
  - **Implementation decisions** that ADR-023 left open, and where the session brief differed: [section 12](#12-implementation-decisions-and-deviations-from-the-session-brief).
  - **Operations:** [TENANT_MERCHANT_EVENT_RELAY.md](../operations/TENANT_MERCHANT_EVENT_RELAY.md).

## 1. How the scope was set

ADR-005 and ADR-012 section 5 defer five items to "the phase that introduces the first event flow":

- the Kafka client library
- topic naming and partitioning
- the schema format
- the outbox implementation
- an inbox or deduplication store

PROJECT_DECISIONS section 13 lists the first two as open decisions of that phase. S3 named the remaining production gate: the first integration event for both tenant origins ([PHASE_3_S3_SELF_SERVICE_ONBOARDING.md section 9](PHASE_3_S3_SELF_SERVICE_ONBOARDING.md#9-residual-risks-and-production-gates)).

**Evidence reviewed (2026-11-23):**

- The baseline: `main` at `f67804f`, equal to `origin/main`, with a clean working tree. CI passed on that commit.
- ADR-005, ADR-012, ADR-013, ADR-014, ADR-018 (E3), ADR-021 and ADR-022.
- PROJECT_DECISIONS sections 7, 9 and 13, SERVICE_CATALOG, SOLUTION_STRUCTURE (BuildingBlocks policy), and the testing guide.
- The Compose Kafka service (`apache/kafka:4.4.0`, KRaft, auto-create off, 3 default partitions, 1 GB memory limit).
- The S1 to S3 write paths: `TenantProvisioningStore`, `TenantOnboardingGate` and `TenantWriteGate`.
- The architecture test `MessagingDeferralTests`.

**Findings:**

- **The repository records no envelope field list, topic, or schema convention.** The Phase 0.1 event catalog is not in the repository (PHASE_3_TENANT_MERCHANT.md section 1). The only binding requirements are those of ADR-012 section 3. The envelope of ADR-023 is therefore a new decision, not a change to an accepted one.
- The first event's name was provisional (`TenantProvisioned`, "or its successor").
- No decision text of an accepted ADR needs to change.

**Owner decisions (2026-11-23, approval of the S4 plan):**

| # | Decision |
|---|---|
| A1 | One event for both creation paths, `TenantCreated`, with `origin` |
| A2 | Membership events deferred to the slice of their first consumer |
| A3 | Producer-side failures halt and alert; a committed fact is never skipped |
| A4 | A root message's `causationId` is the request's correlation ID |
| A5 | Confluent.Kafka; JSON with committed JSON Schema and no registry; `tenant-merchant.events` keyed by the tenant ID; the envelope of ADR-023 section 4 |
| A6 | The outbox of ADR-023 section 5: domain events mapped at `SaveChanges`, a single leader relay, sequential publication |
| A7 | The messaging code stays in TenantMerchant.Infrastructure until a second service publishes |
| A8 | ADR-023 and the contracts are reviewed and accepted before any code |

**Owner review of the proposal (2026-11-23), addressed in the contract and ADR-023 before acceptance:**

1. The committed-creation semantics of `TenantCreated`.
2. The initial owner's identity, evaluated and intentionally excluded.
3. Consumer behaviour for unknown `status` and `origin` values.
4. `displayName` validation aligned with `MerchantDisplayName`, with no white-space-only values.
5. Schema evolution and the relationship between the envelope and payload schemas.
6. The explicit transaction boundary, with asynchronous publication after the commit.

Point 5 led to one refinement of the proposal: a dual-published fact keeps one `eventId`, and the outbox uniqueness is (`EventId`, `EventVersion`). The owner approved it on 2026-11-23.

**Final clarification before acceptance (2026-11-23).** The approved decisions are unchanged.

1. `eventId` is the identity of the logical business fact, not of a serialized message. A message is identified by (`eventId`, `eventVersion`).
2. `eventVersion` is the version of the payload contract.
3. Committing the offset of an unsupported version creates no processed-event record.
4. Deduplication applies a logical fact at most once per consumer, whichever versions are published or supported.
5. Replay after a consumer upgrade is documented, with the retention of processed-event records and the rebuild procedure ([docs/events/README.md](../events/README.md#replay-after-a-consumer-upgrade)).
6. Producer-side fact-identity tests for S4, and consumer acceptance tests C1 to C6 for the slice of the first consumer (section 8).

**ADR-023 accepted by the owner on 2026-11-23.**

**Preferred version per consumer (2026-11-23, before the first commit of the Step 0 documents).** The owner observed that "the first supported version that arrives" makes the result depend on arrival order, which the contract leaves undefined. Clarified as follows:

- Each consumer declares, per event type, its supported versions and **one preferred version**.
- Only the fact's selected version is processed and recorded. A supported but not-selected version is skipped without a processed-event record.
- The selected version is the preferred one when it is published for the fact, otherwise the highest other supported version (the fallback).
- To make that selection possible from any single message, the envelope gains **`publishedVersions`**. No message has been published yet, so it is part of envelope v1 from the start and required.
- A preference change is followed by a gap replay, or by a rebuild ([docs/events/README.md](../events/README.md#changing-the-preferred-version)).
- C1 to C6 now require identical outcomes in both arrival orders.

The outbox uniqueness (`EventId`, `EventVersion`), the fact identity, and the other approved decisions are unchanged.

**In S4:**

- ADR-023, the event conventions, and the `TenantCreated` v1 contract, schema and sample.
- A `TenantCreated` domain event recorded by both creation paths.
- The `OutboxMessages` table, the mapping and envelope serialization at `SaveChanges`, and the relay with its health check and meters.
- Confluent.Kafka in TenantMerchant.Infrastructure only, and the replacement of `MessagingDeferralTests` with the messaging rules.
- Topic provisioning for Compose, the tests and CI.
- Tests against SQL Server 2022, LocalDB and Kafka.

**Not in S4:**

- Membership, invitation, suspension or closure events.
- Any consumer, the consumer loop, inbox tables and dead-letter topics. Their rules are in ADR-023 section 7.
- Messaging in Identity, including the ADR-018 E3 email handoff.
- `BuildingBlocks.Messaging`, a Schema Registry, Kafka transactions and CDC.
- OpenTelemetry trace propagation (observability phase).
- Kafka TLS, SASL and ACLs, and production partitions and retention (deployment phase).
- A backfill of tenants created before S4. No deployed environment exists.

## 2. The flow

```text
POST /internal/v1/tenants  ─┐                                      ┌─► Kafka: tenant-merchant.events
POST /api/v1/tenants       ─┤                                      │    key = tenantId, at least once
                            ▼                                      │
   Tenant.Provision / CreateBySelfService                          │
     records TenantCreated (domain event, internal)                │
                            ▼                                      │
   TenantMerchantDbContext.SaveChangesAsync                        │
     maps public domain events → envelope JSON → OutboxMessages    │
     ── one SQL Server transaction: tenant, profile, owner,        │
        audit record, outbox row ──                                │
                            ▼                                      │
   OutboxRelay (hosted service, one leader per service) ───────────┘
     polls pending rows in Sequence order, publishes one at a time,
     marks the delivered prefix PublishedAt
```

## 3. Write side

- **Domain.**
  - `Tenant` gets a minimal domain-event list (`IHasDomainEvents`, `IDomainEvent` in TenantMerchant.Domain).
  - `Tenant.Provision` and `Tenant.CreateBySelfService` each record one `TenantCreated(TenantId, Slug, DisplayName, Status, Origin, OccurredAt)`.
  - The domain event is internal and is not the contract.
- **Metadata.**
  - The handlers already carry the request's `CorrelationId` (`ProvisionTenantCommand`, `CreateTenantCommand`).
  - They pass a `MessageMetadata(CorrelationId, CausationId)` to the provisioning store and the onboarding session.
  - The endpoints always supply a correlation ID: the middleware generates one when it is absent.
- **Mapping.**
  - Before the base `SaveChangesAsync`, the context collects the tracked aggregates' pending domain events.
  - It maps the public ones with `TenantMerchantIntegrationEvents` into an envelope, adds one `OutboxMessage` per event, and clears the list.
  - With public events but no metadata, it throws, so the transaction rolls back.
- **The transaction boundary.** One SQL Server transaction per creation contains all five rows:

  | Row | Table | Provisioning | Self-service |
  |---|---|---|---|
  | Tenant | `Tenants` | ✓ | ✓ |
  | Merchant profile | `MerchantProfiles` | ✓ | ✓ |
  | Initial `MerchantOwner` membership (`TR_Memberships_RequireActiveOwner` runs inside the transaction) | `Memberships` | ✓ | ✓ |
  | Audit record (`OwnerProvisioned` or `SelfServiceOwnerMembershipCreated`) | `MembershipAuditRecords` | ✓ | ✓ |
  | `TenantCreated` v1 outbox row | `OutboxMessages` | ✓ | ✓ |
  | **Transaction** | | the single `SaveChanges` of `TenantProvisioningStore.AddAsync`, run by EF Core in one transaction | the explicit `READ COMMITTED` transaction of `TenantOnboardingGate`, after `sp_getapplock` on the subject: `SaveChanges`, then `Commit` |

  - Retries (deadlock, unique conflict) start in a fresh scope with new aggregates. A rolled-back attempt leaves no row of any of the five kinds.
- **After the commit, asynchronously.**
  - The HTTP response is returned after the commit and never waits for Kafka.
  - No Kafka I/O happens inside a business transaction.
  - The relay publishes later, from committed rows only. It records `PublishedAt` in its own short statement after the delivery.
- **The event's meaning follows from this boundary.** `TenantCreated` states the complete, committed creation (all four business rows), never an intermediate step ([TENANT_MERCHANT_EVENTS.md](../events/TENANT_MERCHANT_EVENTS.md#semantics-the-complete-committed-creation)).
- **Ordering.** A tenant's later writes are serialized by the tenant row lock (`TenantWriteGate`). A later outbox row of a tenant therefore never commits before an earlier one.

## 4. Outbox table

`dbo.OutboxMessages`, migration `IntegrationEventOutbox`:

| Column | Type | Constraint |
|---|---|---|
| `Sequence` | `bigint IDENTITY` | Clustered primary key |
| `EventId` | `uniqueidentifier` | Unique together with `EventVersion`: `UX_OutboxMessages_EventId_EventVersion`, which allows a later dual publication of one fact (ADR-023 section 3) |
| `Topic` | `varchar(249)` | |
| `MessageKey` | `varchar(64)` | |
| `EventType` | `varchar(128)` | |
| `EventVersion` | `int` | `> 0` |
| `Envelope` | `nvarchar(max)` | `CK_OutboxMessages_EnvelopeSize`: at most 65536 bytes as UTF-8, also checked before the write |
| `OccurredAt` | `datetimeoffset` | |
| `PublishedAt` | `datetimeoffset NULL` | |
| `Attempts` | `int` | Default 0 |
| `LastAttemptAt` | `datetimeoffset NULL` | |
| `LastError` | `nvarchar(512) NULL` | Error kind and code only |

- `IX_OutboxMessages_Pending (Sequence) WHERE PublishedAt IS NULL`.
- `IX_OutboxMessages_Published (PublishedAt) WHERE PublishedAt IS NOT NULL`.
- No foreign keys. The table is technical and never read by business code.

## 5. Relay

`OutboxRelay`, a `BackgroundService` registered by the Infrastructure DI and hosted in TenantMerchant.Api.

**Settings** (`TenantMerchant:Outbox` and `TenantMerchant:Kafka`, environment `TenantMerchant__Outbox__*` and `TenantMerchant__Kafka__*`):

| Setting | Default | Meaning |
|---|---|---|
| `Outbox:Topic` | `tenant-merchant.events` | Written to each new row |
| `Outbox:PollInterval` | 1 s | Idle polling interval |
| `Outbox:BatchSize` | 100 | Rows per batch |
| `Outbox:MaxBackoff` | 60 s | Cap of the exponential backoff (1 s base, jitter) |
| `Outbox:HaltAfterAttempts` | 10 | Consecutive failures of the head row before the relay reports itself halted (implementation decision D3) |
| `Outbox:PublishedRetention` | 7 days | Published rows older than this are deleted |
| `Outbox:CleanupInterval` | 1 h | |
| `Outbox:StaleAfter` | 5 min | The health check reports `Degraded` beyond this pending age |
| `Kafka:BootstrapServers` | none | Required outside Development. Without it in Development, the relay is disabled with a warning. |
| `Kafka:SecurityProtocol`, `Kafka:SslCaLocation`, `Kafka:SslCertificateLocation`, `Kafka:SslKeyLocation` | `Plaintext`, none | Outside Development only `Ssl` with a client certificate and key (mutual TLS) is accepted (D5). SASL is a production gate. |
| `Kafka:MessageTimeout` | 30 s | `message.timeout.ms`: the bound of one delivery |

**Cycle:**

1. **Leadership.**
   - The relay acquires `sp_getapplock @Resource = N'outbox-relay', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0` on a dedicated, long-lived connection.
   - If the lock is not granted, the instance is a standby: it retries every 5 poll intervals and publishes nothing.
   - The relay checks the lock connection every cycle. A broken connection ends the leadership at once.
2. **Batch.**
   - The batch query: `SELECT TOP (@batch) ... FROM OutboxMessages WITH (READPAST) WHERE PublishedAt IS NULL ORDER BY Sequence`.
   - `READPAST` skips uncommitted rows, which the next poll takes.
3. **Publish.**
   - Each row is produced to its `Topic` with key `MessageKey`, the envelope bytes as the value, and the headers of ADR-023 section 4.
   - The relay **awaits each delivery report** before it sends the next row.
   - Producer settings: `acks=all`, `enable.idempotence=true`, `message.timeout.ms=30000`, `client.id=tenant-merchant-outbox-relay`.
4. **Mark.**
   - One `UPDATE ... SET PublishedAt = @now WHERE Sequence IN (delivered prefix)`.
   - On a failure, the head row gets `Attempts + 1`, `LastAttemptAt` and `LastError`. The relay backs off, and the batch ends there.
5. **Drain.** A full batch starts the next one at once. Otherwise the relay waits `PollInterval`.
6. **Cleanup.** At most once per `CleanupInterval`, the leader runs `DELETE TOP (1000)` of published rows older than the retention, until none is left.

**Startup:** AdminClient metadata confirms that the topic exists. A missing topic is logged as an error and keeps the relay in backoff. **The relay never creates a topic.**

**Shutdown:** see ADR-023 section 5. A row delivered but not yet marked is published again by the next leader.

## 6. Guarantees

| Property | Guarantee |
|---|---|
| Atomicity | An outbox row commits if and only if its business change commits |
| Delivery | At least once. Duplicates have the same `eventId` and bytes. Exactly-once is not provided. |
| Order | Per tenant, in commit order, within one partition. There is no order across tenants. |
| Availability | Kafka being down never fails a business request |
| Loss | A committed row is never skipped or deleted before it is published |

## 7. Failure handling and operations

| Situation | Behaviour | Visible as |
|---|---|---|
| Kafka down | Backoff of 1 s to 60 s with jitter; the backlog drains in order afterwards | Warning logs; `outbox` health `Degraded` after `StaleAfter`; pending age metric |
| Topic missing or not authorized | Backoff, never created or skipped | Error log; `Degraded` |
| A row that cannot be published | The relay halts at the row | Error log every 10 attempts with the event ID and type; `Degraded` |
| Relay crash, restart or handover | The next leader resumes at the oldest pending row | A duplicate is possible |
| Database down | Backoff | Readiness `Unhealthy` (existing check) |

**Observability:**

- **Log events 3300 to 3349:** leadership acquired or lost, batch published, publish failed, backoff, topic missing, cleanup, halted. They carry event IDs and types, never the envelope.
- **Health:** the `outbox` check is registered **without the `ready` tag**, so `/health/ready` never runs it and Kafka never removes the API from rotation.
  - It is exposed on its own anonymous path, `/health/outbox`.
  - It reports `Degraded` when the oldest pending row is older than `StaleAfter` or the relay is backing off.
  - Like the existing checks, it answers with the status word only.
- **Meters** (`EnterpriseCommerce.TenantMerchant.Outbox`):
  - `outbox.pending` and `outbox.oldest_pending_age_seconds` (observable)
  - `outbox.published`, `outbox.publish_failures` (tag `category`) and `outbox.halts` (tag `category`) (counters)
  - `outbox.publish.duration` and `outbox.cycle.duration` (histograms, seconds)
  - `outbox.leader` and `outbox.halted` (observable, 0 or 1)
  - no tenant or event IDs as tags

  No exporter is connected until the observability phase.

**Runbook (a halted relay):**

1. Find the head row: `SELECT TOP (1) Sequence, EventId, EventType, Attempts, LastError FROM OutboxMessages WHERE PublishedAt IS NULL ORDER BY Sequence`.
2. Fix the cause: the topic, the ACL, the configuration or the broker. The relay resumes by itself.
3. Only if the row itself can never be published, and only after a documented review: the row's fact must be republished in a corrected form before the row is marked. Never delete a pending row.

**Topic provisioning:**

- **Locally:** a manifest `deploy/docker/kafka/topics.txt` and a one-shot `kafka-init` service in the `kafka` profile, which runs `kafka/create-topics.sh` (`kafka-topics.sh --create --if-not-exists`).
  - **Verified (step 5):** `docker compose up -d --wait` accepts the one-shot container, but it does **not** wait for it to finish: it returned while `kafka-init` was still running.
  - `docker compose --profile kafka wait kafka-init` blocks until it exits and returns its exit code (0). It is the documented confirmation.
  - A second run leaves the existing topic unchanged.
- **Tests:** each test creates a unique topic with AdminClient and deletes it afterwards.
- **Deployed environments:** infrastructure as code (deployment phase).

## 8. Test plan

Infrastructure tests follow the existing rule: they skip when infrastructure is not configured, and fail when `EC_REQUIRE_INFRA_TESTS=true`. New variables:

| Variable | Needed by | Example (local) |
|---|---|---|
| `EC_TEST_KAFKA` | Kafka integration tests | `127.0.0.1:9094` (`--profile kafka`) |
| `EC_TEST_KAFKA_RESTART_CONTAINER` | The Kafka outage test (serial collection) | `enterprisecommerce-kafka-1` |

**Unit (TenantMerchant.UnitTests):**

- Each factory method records exactly one domain event with the tenant's values.
- The mapper fills every envelope field. The key is the tenant ID. `causationId` is the correlation ID.
- **Sensitive data:** the serialized message contains neither the subject, the client ID, the idempotency key, the request hash nor the actor reference.
- Envelope formats: camelCase, UTC `Z`, lowercase GUIDs, string enums, the size limit.
- Relay logic against fake store and producer seams:
  - the head-of-line halt
  - prefix marking
  - backoff growth and cap
  - a standby that does not publish
  - cancellation that marks the delivered prefix
- **Contracts:**
  - the `JsonSchemaExporter` output against the committed schema
  - golden-sample round trips
  - envelope conformance

  The committed schema files may be normalized once to the exporter's canonical form in step 3, without a semantic change. The hand-written `pattern`, length and token constraints stay; the snapshot test compares the generated structure and keeps the committed constraints.
- **Payload validation aligned with the domain.**
  - A test passes display names through `MerchantDisplayName.TryCreate`, then through the mapper, and validates the result against the committed schema with a JSON Schema validator. The inputs are leading and trailing white space of every .NET white-space character, interior spaces, non-BMP characters, U+FEFF, and 200 and 201 code units.
  - A test checks that a white-space-only name is refused before any write.
  - The choice of test-only validator package is evaluated in step 3 (licence, maintenance) and recorded in `Directory.Packages.props`.

**SQL Server (TenantMerchant.IntegrationTests; Compose SQL Server 2022 and LocalDB):**

- **Atomicity.**
  - Provisioning and self-service each commit exactly one outbox row together with the tenant, profile, owner membership and audit record.
  - The test injects a rejecting check constraint, as in the S3 audit test, once on the audit table and once on `OutboxMessages`.
  - In both cases the request answers `503` and **none of the five rows** remains. After the constraint is removed, a retry with the same key succeeds and emits exactly one row.
- **No publication before the commit.** While a creation's transaction is held open in a test, the relay publishes nothing for it.
- **No row** for a replay, a slug conflict, a limit refusal, a reused key, or a rolled-back deadlock attempt.
- **Concurrency.** The existing 8-way creation races give exactly one row per created tenant.
- **Missing metadata** fails closed.
- **Leadership.** Of two relays on one database, one publishes. Killing the leader's lock connection moves the leadership.
- **Cleanup** removes only published rows past retention.
- **The migration** applies down and up.

**Kafka (SQL Server and Kafka):**

- **End to end.** `POST /api/v1/tenants` and `POST /internal/v1/tenants` each produce one `TenantCreated`. The test checks the envelope, the key, the headers and that the body matches the schema.
- **Ordering.** Synthetic rows of one key arrive in `Sequence` order on one partition. Several keys spread over the partitions.
- **Duplicates.** A crash hook fires after delivery and before marking. After a restart, the same `eventId` is published again with identical bytes.
- **Restart and recovery.** The host stops with rows pending, and a new host publishes all of them.
- **Kafka down, then restored.**
  - The test stops the container, as the Redis restart test does.
  - The API still answers `201`, the attempts grow, and health reports `Degraded`.
  - After the restart, the backlog drains in order.
  - A TCP forwarder cannot simulate the outage: the client would reconnect to the advertised listener (`127.0.0.1:9094` since TS-5; `localhost:9094` before) past it.
- **A missing topic** shows up in the log and the health check, and no topic is created.

**Fact identity and dual publication, producer side (S4; SQL Server and Kafka):**

S4 has no `TenantCreated` v2. These tests use synthetic outbox rows written directly to the table.

- The outbox accepts two rows with the same `EventId` and different `EventVersion`. It refuses a second row with the same (`EventId`, `EventVersion`) through `UX_OutboxMessages_EventId_EventVersion`.
- The relay publishes both rows of one fact with the same key and therefore to the same partition. Both carry the same `eventId` in the body and the `event-id` header, and each its own `eventVersion` and `event-version` header.
- A redelivery after a crash between delivery and marking repeats the same (`eventId`, `eventVersion`) with identical bytes. It never creates a new `eventId`.
- Every `TenantCreated` v1 envelope written by the mapper carries `publishedVersions: [1]`.
- Both synthetic rows of a dual-published fact carry the same `publishedVersions` (`[1, 2]`). An envelope whose `eventVersion` is not in its `publishedVersions` is refused before the write.

**Consumer acceptance tests (required in the slice of the first consumer).**

The approved S4 scope contains no consumer or inbox code, so these tests are planned here and bind that slice. ADR-023 section 7 refers to them. They run against the consumer's real database and a real Kafka topic. The producer side is a test producer that writes envelopes of the committed contracts, with a synthetic v2 of one event for the version cases.

**Determinism requirement for every scenario:** a scenario with two versions of one fact runs **in both arrival orders** (v1 then v2, and v2 then v1) against fresh state. Both runs must end with:
- the **same projection state**, compared field by field
- **the same processed-event record**, with the same `EventVersion`
- the same counts of applied, skipped-as-not-selected and duplicate messages

`S` is the set of supported versions and `p` the preferred version.

| # | Scenario | Expected (both arrival orders) |
|---|---|---|
| C1 | **v1 supported, v2 unsupported** (`S = {1}`, `p = 1`); one fact dual-published, `P = [1, 2]` | Applied once, from v1. The v2 message is acknowledged (offset committed) with **no** processed-event record. One record, `EventVersion = 1`. |
| C2 | **v1 unsupported, v2 supported** (`S = {2}`, `p = 2`); the same fact | Applied once, from v2. The v1 message is acknowledged with no record. One record, `EventVersion = 2`. |
| C3a | **Both supported, v2 preferred** (`S = {1, 2}`, `p = 2`); the same fact | **Always applied from v2.** v2 first: v2 is applied, and the later v1 is skipped as not selected. v1 first: v1 is skipped without a record, and v2 is applied when it arrives. One record, `EventVersion = 2`. The projection is identical in both orders. |
| C3b | **Both supported, v1 preferred** (`S = {1, 2}`, `p = 1`); the same fact | Always applied from v1, as C3a with the roles swapped. One record, `EventVersion = 1`. |
| C3c | **Fallback** (`S = {1, 2}`, `p = 2`): one fact published only as v1 (`P = [1]`), one only as v2 (`P = [2]`) | The v1-only fact is applied from v1, the v2-only fact from v2. A non-preferred version is never skipped when it is the only published version. |
| C3d | **Inconsistent envelope**: `eventVersion` 2 with `publishedVersions: [1]` | Dead-lettered as malformed. No record, no effect. |
| C4a | **Duplicate delivery of the selected version** (`S = {1, 2}`, `p = 2`): v2 delivered twice, including concurrently on two consumer instances during a rebalance | Applied once. The unique key rejects the second record, and its transaction rolls back without effect. |
| C4b | **Duplicate delivery of the not-selected version**: v1 delivered twice, before and after v2 | Every v1 delivery is skipped without a record. The fact is applied once, from v2. |
| C4c | **Duplicate of either version after a crash**: the effect committed, the offset not committed, then a restart. Run for v1 and for v2 under each declaration of C1 to C3. | Redelivered and skipped. No second effect, and the record's `EventVersion` is unchanged. |
| C5a | **Replay after an upgrade** (`S = {1}` → `S = {1, 2}`, `p = 2`). Before: one dual-published fact and one v2-only fact. The upgraded consumer resets offsets to the earliest and replays. | The dual-published fact is **not** applied again: its v1 record exists, and it keeps its v1-derived data. The v2-only fact, acknowledged earlier without a record, is applied now. One record per fact. |
| C5b | **Replay after an upgrade** (`S = {2}` → `S = {1, 2}`, `p = 2`). Before: a v1-only fact and a dual-published fact. | The v1-only fact is applied (fallback). The dual-published fact is skipped. |
| C5c | **Preference change with a split pair** (`S = {1, 2}`): stop after consuming a dual-published fact's v1 under `p = 2` (skipped), but before its v2; change to `p = 1`; restart. Run once **without** and once **with** the required gap replay. | Without the replay, the fact is applied from neither version: the test documents the hazard. With the replay from before the switch point, the fact is applied once, from v1. Facts applied before the change are deduplicated. |
| C5d | **Preference change, then rebuild** (`p = 1` → `p = 2`): the projection and its records are deleted in one transaction, then a replay from the earliest offset | Every retained fact is applied exactly once: dual-published facts from v2 and v1-only facts from v1 (fallback). The projection equals the one a consumer with `p = 2` from the start would have built. Deleting only one of the two (projection or records) is refused by the rebuild operation. |
| C5e | **Records past retention**, in a test with a shortened retention | Documents the hazard: a fact whose record was cleaned up is applied again. The test asserts that the cleanup never removes records younger than the configured minimum (topic retention plus the replay window). |
| C6 | **Unsupported event type**, then support is added and the consumer replays | Acknowledged without a record at first, applied on replay, from its selected version |

**Architecture (replacing `MessagingDeferralTests`):**

- Only `EnterpriseCommerce.TenantMerchant.Infrastructure` declares `Confluent.Kafka`.
- No project declares another messaging framework or `Confluent.SchemaRegistry`.
- No Domain or Application assembly references a Kafka assembly.
- Outbox and integration-event types exist only in TenantMerchant.Infrastructure. Identity has none.

**Regression:** the full suite (1094 tests at S3) passes unchanged.

**CI:**

- A step starts `apache/kafka:4.4.0` as a named container (`docker run --name kafka`) with a throwaway `CLUSTER_ID`, and waits for it to be healthy.
- `EC_TEST_KAFKA` and `EC_TEST_KAFKA_RESTART_CONTAINER=kafka` are set for the test step.

**Local resources:** the Docker VM (about 3.8 GB) holds SQL Server (2560 MB limit) and Kafka (1 GB) together only barely.

- Run the Kafka suites with LocalDB and the Compose Kafka.
- Run them once with the Compose SQL Server 2022 and Kafka, with `MSSQL_MEMORY_LIMIT_MB` lowered if needed.
- CI runs SQL Server 2022 and Kafka on every push.

## 9. Implementation sequence

| Step | Content | Gate |
|---|---|---|
| 0 | ADR-023, the conventions, the contract, the schemas and sample, this document, status fixes | **Done:** ADR-023 accepted on 2026-11-23 |
| 1 | Confluent.Kafka in `Directory.Packages.props`; the messaging architecture rules | **Done** (section 10): build, architecture tests, full suite |
| 2 | Domain events on `Tenant` | **Done:** unit tests |
| 3 | `OutboxMessage`, the mapper, the envelope serializer, the `SaveChanges` hook, the metadata path, the migration `IntegrationEventOutbox` | **Done:** SQL Server atomicity tests, contract tests |
| 4 | The relay, the producer adapter, leadership, cleanup, options validation, the health check, meters | **Done:** unit tests, SQL Server leadership tests |
| 5 | Compose `kafka-init` and the topic manifest; Kafka integration and outage tests; the CI Kafka step | **Done:** Kafka tests locally; the CI step is committed and runs on the next push |
| 6 | Evidence, doc updates (PROJECT_DECISIONS, SERVICE_CATALOG, CLAUDE.md, the testing guide, the Compose README) | **Done:** section 10 |

On acceptance (done 2026-11-23):

- its status changed to Accepted
- the status lines of ADR-005 and ADR-012 point to it
- PROJECT_DECISIONS sections 2, 3, 9 and 13 record the resolution

## 10. Test evidence

### Step 1: the Kafka client package and the messaging architecture rules (2026-11-23)

**Package.**
- `Confluent.Kafka` **2.16.0** is pinned in `Directory.Packages.props`, with a justification comment. It was the latest stable release, published on 2026-10-07.
- Its `net10.0` target has a single dependency, `librdkafka.redist` **2.16.0**.
- Licences: Apache-2.0 for the client. BSD-2-Clause for librdkafka, whose bundled components (`LICENSES.txt`) are under permissive licences.
- Native builds: win-x64, win-x86, linux-x64, linux-arm64, linux-s390x, osx-x64 and osx-arm64.
- Restore runs with NuGet audit (`all`, level `low`, warnings as errors) and reports no vulnerable package in any project.
- `EnterpriseCommerce.TenantMerchant.Infrastructure` is the only project that references it. No code uses it yet, so no assembly references it when compiled.

**Rules.** `MessagingBoundaryTests` replaces `MessagingDeferralTests` (19 cases) with 39 cases:

| Rule | Cases |
|---|---|
| `Confluent.Kafka` is declared only by an allow-listed Infrastructure project (today `EnterpriseCommerce.TenantMerchant.Infrastructure`) | 11, one per `src` project |
| The allow-list names only existing Infrastructure-layer projects | 1 |
| No project declares a package ADR-023 rejects: MassTransit, NServiceBus, Rebus, Wolverine, DotNetCore.CAP, KafkaFlow, any other `Confluent.*` (Schema Registry, serializers), Apache.Avro, CloudNative.CloudEvents | 11 |
| No compiled service assembly references `Confluent.Kafka` except the allow-listed ones. This adds the Api layer to the existing Domain and Application rules (ADR-005: Kafka client code only in Infrastructure). | 8 |
| Types named `Outbox`, `IntegrationEvent`, `EventEnvelope`, `KafkaProducer` or `KafkaConsumer` exist only in the allow-listed Infrastructure assemblies | 8 |

**Mutation checks.** Each was applied temporarily, then reverted:

| Mutation | Result |
|---|---|
| `Confluent.Kafka` declared by `Identity.Infrastructure` | Only `KafkaClient_IsDeclaredOnlyByAllowListedInfrastructureProjects(Identity.Infrastructure)` fails |
| `Confluent.SchemaRegistry` declared by `TenantMerchant.Infrastructure` | Only `NoProject_DeclaresARejectedMessagingPackage(TenantMerchant.Infrastructure)` fails |
| A type `OutboxProbe` in `TenantMerchant.Application` | Only `MessagingTypes_ExistOnlyInAllowListedInfrastructureAssemblies(TenantMerchant.Application)` fails |
| `TenantMerchant.Api` compiled against a `Confluent.Kafka` type | Only `KafkaClient_IsReferencedOnlyByAllowListedInfrastructureAssemblies(TenantMerchant.Api)` fails |

**Results:**

- **Build:** `dotnet build EnterpriseCommerce.sln -c Release`: 0 warnings, 0 errors.
- **Architecture tests:** 97 passed (S3: 77; +39 new, −19 removed).
- **Full suite**, run with:
  - SQL Server 2022 (Compose `sqlserver`) and the session Redis (Compose `bff`)
  - `EC_REQUIRE_INFRA_TESTS=true`, `EC_TEST_BROWSERS=true` and `EC_TEST_REDIS_RESTART_CONTAINER` set

  The result was **1114 tests: 1113 passed, 1 failed, 0 skipped**.
  - The failure was the browser test `AfterAPasswordChange_TheBffReauthorizesSilently` (Chromium), a Playwright 30-second load-state timeout under the parallel load. It is the same flaky test as at S3, recorded as known issue [TS-1](../testing/README.md#ts-1-30-second-navigation-timeouts-after-a-form-submission-open).
  - In two isolated runs of the browser suite, all **18 of 18** passed.
  - Step 1 changes no BFF or Identity code.
- **Secret scan** of the test output (the CI patterns): clean.

**Finding (pre-existing, not caused by step 1).** Locally, a configuration-less `dotnet restore EnterpriseCommerce.sln` followed by `dotnet build -c Release --no-restore` puts the Debug-only `EnterpriseCommerce.Identity.DevMailSink` into the restore graph. The build then copies it into the ArchitectureTests Release output, and `ReleaseBuild_IdentityApi_DoesNotReferenceOrShipTheMailSink` fails.
- This was reproduced on the unchanged S3 baseline (`f67804f`) as well.
- `dotnet restore -p:Configuration=Release`, or letting `dotnet build`/`dotnet test -c Release` restore, avoids it.
- **Explained and fixed after the push (2026-11-23, finding [CI-1](../testing/README.md#ci-1-test-steps-could-not-fail-on-a-test-failure-fixed-2026-11-23)):** CI *was* affected. Its test steps ran without `pipefail`, so the piped `dotnet test` could not fail the step, and the failing test was hidden. Linux replays with the pinned SDK reproduce the failure. CI now restores for Release, and its test steps propagate failures. This closes the item that was to be checked before the CI changes of step 5.

### Steps 2 to 6: outbox, relay, Kafka tests and CI (2026-11-23)

**What was built.** Code map:
- `TenantMerchant.Domain/Common/DomainEvents.cs` and `Tenants/TenantCreated.cs`
- `TenantMerchant.Application/Common/MessageMetadata.cs`
- `TenantMerchant.Infrastructure/Messaging/`: the outbox entity, envelope, serializer, mapper, options, relay, Kafka publisher, leadership, store, state, metrics and health check. Since Phase 5 everything except the mapper and the service's profile lives in `EnterpriseCommerce.BuildingBlocks.Messaging` ([ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) section 4).
- the migration `IntegrationEventOutbox`
- `/health/outbox`
- `deploy/docker/kafka/` and the `kafka-init` service
- the CI Kafka container

**The flow, from creation to acknowledgement:**

1. A creation request (`POST /internal/v1/tenants` or `POST /api/v1/tenants`) reaches its handler, which builds the aggregates. `Tenant.Provision` or `Tenant.CreateBySelfService` records `TenantCreated`.
2. The handler passes `MessageMetadata.ForRequest(correlationId)` to the store or session.
3. `TenantMerchantDbContext.SaveChanges` maps the event to one envelope per published version:
   - `eventId` = `Guid.CreateVersion7(occurredAt)`, once per fact
   - `publishedVersions` = `[1]`

   It adds the `OutboxMessages` row before the base save.
4. One SQL Server transaction commits the five rows: the single `SaveChanges` of provisioning, or the onboarding gate's `READ COMMITTED` transaction. The HTTP response follows.
5. The leader relay reads committed pending rows (`READPAST`, `Sequence` order) and produces each row to `Topic`:
   - key `MessageKey` (the tenant ID)
   - value: the envelope's UTF-8 bytes
   - the five headers
6. The relay awaits the broker's acknowledgement (`acks=all`, idempotent producer), then the next row.
7. After the batch, one `UPDATE` sets `PublishedAt` on the acknowledged prefix.

**Commits** (on `main` after `bd24896`, not pushed):

| Commit | Content |
|---|---|
| `f4d0e55` | The outbox table, the envelope, the serializer, the mapper and the contract tests (steps 2 and 3) |
| `84ef3db` | `TenantCreated` in both creation transactions (step 3) |
| `0fce93b` | The leader relay and the Kafka publisher (step 4) |
| `1644333` | Outbox health and configuration validation (step 4) |
| `7f184d8` | The Compose topic manifest and `kafka-init` (step 5) |
| `1907b9a` | Kafka integration tests and the CI Kafka step (step 5) |
| `ab285cf` | Fix: the relay reads its settings on use, not in its constructor (found by the full run, below) |
| `aec77a2` | The relay settings tested through the startup validator ([TS-3](../testing/README.md#ts-3-a-host-that-fails-to-start-can-surface-objectdisposedexception-in-webapplicationfactory-worked-around-for-the-relay-settings)) |
| this commit | Documentation and evidence (step 6) |

**Tests added** (counts from the final runs):

| Project | S4 step 1 | Now | What the new tests prove |
|---|---|---|---|
| TenantMerchant.UnitTests | 266 | 457 | The domain events. The contract against the committed schemas (Corvus.Json.Validator), the exporter snapshot, the golden sample, the display-name differential over every BMP character in three positions, sensitive-data exclusion and dual-fact identity. The relay logic against fakes. The Kafka record, the producer settings and the error categories. The options rules and the startup validation. The health check states. The meters. |
| TenantMerchant.IntegrationTests | 162 | 200 | **SQL Server:**<br>- one row per creation, with the request's or the generated correlation ID<br>- none for replays and refusals<br>- rollback through the audit and outbox tables (five rows or none, then exactly one on retry)<br>- the 8-way races and the held lock<br>- missing metadata<br>- an unpaired surrogate (`400`)<br>- the table constraints and the guarded migration<br>- committed-only reads, a rolled-back row, marking and cleanup<br>- two relays, a killed leader session, a crash before marking, a hosted relay<br>- `/health/outbox` against readiness<br>**Kafka:** end to end, order, dual facts, redelivery with identical bytes, a new relay, graceful shutdown, a missing topic, the real outage. |
| ArchitectureTests | 97 | 97 | Unchanged and passing. `Confluent.Kafka` is compiled into TenantMerchant.Infrastructure only, and every messaging type lives there. |

**Full verification** (Windows, Release):
- **Infrastructure:** Compose SQL Server 2022 (`MSSQL_MEMORY_LIMIT_MB=1536`), the session Redis and Kafka 4.4.0 together. `EC_REQUIRE_INFRA_TESTS=true`, `EC_TEST_BROWSERS=true`, and both restart containers set.
- **Restore** with `-p:Configuration=Release`. **Build:** 0 warnings, 0 errors.
- **Exit codes:** captured without pipes.

| Run | Result | Cause and action |
|---|---|---|
| 1 | 1341 tests: 1339 passed, **2 failed**, 0 skipped | `InvalidRelaySettings_StopTheHostFromStarting` (the two outbox cases): `ObjectDisposedException` instead of the validation message. **A defect found:** the relay read its settings in its constructor, and hosted services are resolved before the startup validation. Fixed in `ab285cf`, with a unit test. |
| 2 | 1342 tests: 1340 passed, **2 failed**, 0 skipped | The same test, now the Kafka case: the race is in the test host's teardown, not in the validation ([TS-3](../testing/README.md#ts-3-a-host-that-fails-to-start-can-surface-objectdisposedexception-in-webapplicationfactory-worked-around-for-the-relay-settings)). The cases moved to the host's `IStartupValidator` in `aec77a2`. Also `AfterAPasswordChange_TheBffReauthorizesSilently` (Chromium), the 30-second timeout of [TS-1](../testing/README.md#ts-1-30-second-navigation-timeouts-after-a-form-submission-open). S4 changes no BFF or Identity code. |
| 3 | **1343 tests: 1343 passed, 0 failed, 0 skipped** | Final code. The browser suite passed 18 of 18 in this run. |

- **TenantMerchant suites against LocalDB and the Compose Kafka** (final code): unit 457, integration 200 and architecture 97, all passed, none skipped.
- **Secret scan** of the three test logs (the CI patterns): clean.
- **Docker Compose:** `config --quiet` for every profile. `kafka-init` was verified (exit code 0, and a second run is idempotent). The topic has 3 partitions, replication factor 1, `cleanup.policy=delete` and `retention.ms=604800000`.
- **The CI Kafka step:** its script was executed locally under another container name and port: the broker started and answered the health wait. It runs in GitHub Actions on the next push; that run has not happened yet.
- **Not measured:** throughput, latency and memory under load. No performance claim is made.

### Design review of the contracts

S4 was not implemented at the time of the review. Only the committed contract files were checked, during the design review of 2026-11-23:

- **Validator:** Ajv 8.17.1 with ajv-formats 3.0.1 (JSON Schema 2020-12, ECMA-262 regular expressions with the `u` flag, `strict` mode). It was installed outside the repository; nothing was added to the solution.
- **The golden sample** validates against `envelope.v1.schema.json`, and its payload against `TenantCreated.v1.schema.json`.
- **29 targeted checks passed:**
  - white-space-only, empty, leading and trailing white-space and control-character display names are refused
  - interior spaces, non-Latin and astral characters, U+FEFF, and 200 characters are accepted; 201 are refused
  - unknown `status` and `origin` tokens are accepted; `active` and `""` are refused
  - slug edge cases
  - added optional payload and envelope properties are accepted
  - missing required properties, a non-version-7 `eventId`, an uppercase GUID, a non-UTC timestamp and `eventVersion` 0 are refused
- **Differential check against the domain rule:**
  - `MerchantDisplayName.TryCreate` was applied to every BMP code point, alone and in three positions (253,952 cases), and a value was counted as emittable when it is valid and already in stored form.
  - The schema agreed in every case (0 mismatches).
  - .NET's `char.IsWhiteSpace` and `char.IsControl` sets were listed from the runtime and match the schema's character classes exactly.
- **System.Text.Json** refuses an unpaired surrogate when reading and when writing (`InvalidOperationException`), so none can reach a message.

## 11. Residual risks and production gates

**Residual risks (accepted with the design):**

- At-least-once delivery: every consumer must deduplicate.
- A row that can never be published halts the relay until an operator acts. Halting is chosen over losing order or facts.
- Sequential publication by one leader bounds throughput. Enough for Tenant & Merchant; not measured.
- Schema compatibility is enforced by tests and review, not by a registry.
- `displayName` may be a person's name (a sole trader). It is public profile data, and topic retention bounds how long it stays in Kafka. Subject IDs are deliberately not published.
- **Failing closed on unknown open-set values** is a consumer obligation that the producer cannot enforce. It is checked in each consumer's own tests and review.
- **Dual publication with one `eventId`** relies on consumers acknowledging unsupported versions without an inbox row. That rule is part of the consumer contract and must be tested in the first consumer.
- **Verified in step 3 (resolved):** a request body with an escaped unpaired surrogate in `displayName` is refused with `400` by both creation endpoints, and nothing is written (`TenantCreatedOutboxTests`). The `InvalidOperationException` of the isolated check does not reach the endpoints, so there is no S1 to S3 defect.
- **A frozen leader:** a relay process that hangs while its SQL session stays open keeps the leadership, and nothing is published until it is restarted. A crashed process or a broken connection releases the lock at once (tested). See D1.
- **Deadlock retries** of self-service onboarding rebuild the aggregates in a fresh scope, so a rolled-back attempt leaves no outbox row. This is structural and is covered by the rollback and held-lock tests. A deadlock cannot be forced deterministically, and no test injects one.
- **The meter's observable gauges** report the hosted relay of the process. Several hosts in one process, as in the tests, share one meter.

**Production gates (open):**

- Kafka authentication: mutual TLS is supported (D5). SASL needs a secret store design, because the service holds no credential. ACLs: the producer may write only its topic.
- Replication factor 3, `min.insync.replicas=2`, and the partition count and retention of `tenant-merchant.events`.
- Topics as infrastructure as code.
- Alerting on the `outbox` `Degraded` state and the pending-age metric.
- Load testing of the relay beyond the local tests.
- A frozen-leader mitigation beyond liveness restarts, if measured operation shows a need (D1).
- An exporter for the meters, and OpenTelemetry trace propagation (observability phase).

## 12. Implementation decisions and deviations from the session brief

**Implementation decisions.** ADR-023 left these operational details open. Each is the smallest choice that keeps ADR-023 unchanged, and none changes a decision of the ADR.

| # | Decision | Why |
|---|---|---|
| D1 | **The leadership lease is the SQL session.** `sp_getapplock` (`Session`, timeout 0) is held on a dedicated, unpooled connection (application name `tenant-merchant-outbox-relay`). The leader checks `APPLOCK_MODE` on every cycle, and a broken connection ends its leadership at once. A process crash or network loss ends the session, and a standby takes over on its next attempt (every 5 poll intervals). | It is the lock of ADR-023 section 5. No lease column or claim table is needed. Known limit: a frozen process with an open session keeps the lock (section 11). |
| D2 | **Failure categories.** Broker errors map to `transient`, `topic`, `authorization`, `rejected`, `configuration` or `unknown`. `LastError` stores `category:code`, never a message or payload. Metrics use the category as their only tag. | A bounded, loggable value for operators and metrics, which distinguishes transient from permanent failures |
| D3 | **The halted state.** A permanent category (`topic`, `authorization`, `rejected`, `configuration`), or `HaltAfterAttempts` (10) consecutive failures of the head row, marks the relay halted:<br>- Error log 3312, then 3313 every 10th attempt<br>- `outbox.halts` and `outbox.halted`<br>- `/health/outbox` `Degraded`<br>The relay keeps retrying at the capped backoff and resumes by itself after the fix. | ADR-023 section 6: halt and alert, never skip. Retrying at a bounded rate makes the relay recover without a restart. |
| D4 | **The backoff survives restarts.** The wait before the next attempt is derived from the row's stored `Attempts` and `LastAttemptAt`: `min(MaxBackoff, 1 s × 2^(attempts−1))` with jitter of 50 to 100 %. | No retry storm after a restart, and no in-memory state is needed |
| D5 | **Kafka security outside Development is mutual TLS** (`Ssl` with a client certificate and key, files mounted as secrets). SASL is not supported in S4. | ADR-023 section 6 refuses an unencrypted, unauthenticated connection. The service may hold no credential (`ServiceOwnershipTests`), and a SASL secret needs a secret store design (production gate). |
| D6 | **Producer settings:**<br>- `linger.ms=0`, because each message is awaited alone<br>- `allow.auto.create.topics=false`<br>- a producer that reaches a fatal state is rebuilt for the next attempt | Latency, ADR-023 section 2 (no application creates a topic), and recovery of the idempotent producer |
| D7 | **Metrics and health additions** to section 7:<br>- `outbox.halts`, `outbox.halted`, `outbox.publish.duration` and `outbox.cycle.duration`<br>- the health check reads the backlog from the database, so standbys report it as well<br>- a database failure of the check is `Degraded` | Visibility of the terminal state and of processing time, with bounded tags |
| D8 | **The JSON Schema validator for tests is Corvus.Json.Validator** (Apache-2.0). JsonSchema.Net's current releases ship under a custom EULA (OSMFEULA), and NJsonSchema targets draft 4. | Section 8 left the package choice to step 3 |
| D9 | **The outbox row is technical.** It has no `rowversion`. It is updated only by set-based statements of the single leader, conditioned on `PublishedAt IS NULL`. The migration's `Down` refuses while rows are pending (error 50004). | A pending row is a committed fact and is never discarded |
| D10 | **Synthetic dual-version rows only in tests.** The mapper publishes `TenantCreated` in `[1]` only. The serializer computes `publishedVersions` once per fact from its version list, so a future dual publication only adds a list entry and its payload mapping. | No speculative v2 (session brief, Milestone D) |

**Deviations from the session brief.** The brief asked for some behaviour that ADR-023 decides otherwise. As the brief requires, the accepted decision was followed and the difference is recorded here.

| The brief asked | ADR-023 decides | Implemented |
|---|---|---|
| A database-backed claim or lease with a bounded duration, so that multiple workers can claim rows | One leader per service through a session application lock. Row claiming (`UPDLOCK, READPAST`) is explicitly rejected, because it breaks per-key order (Alternatives). | The single leader of D1. Abandoned work is recovered by the next leader from the oldest pending row. |
| A poison event must not block unrelated events indefinitely; a dead-letter mechanism | No automatic skipping on the producer side. A producer-side dead-letter topic or parking is rejected, because it breaks per-tenant order and hides lost facts (section 6). | A halt with visible state and a documented runbook (D3, [TENANT_MERCHANT_EVENT_RELAY.md section 6](../operations/TENANT_MERCHANT_EVENT_RELAY.md#6-failures-and-recovery)). No event is skipped, deleted or dead-lettered. |
| Bounded retries, no infinite retry loop | Back off and retry until an operator fixes the cause | Retries are bounded **in rate** (at most one per `MaxBackoff`), not in count. After `HaltAfterAttempts` the relay is reported halted and stays observable. |
| Readiness checks for the relay's dependencies | The `outbox` check is never part of readiness | The database stays in `/health/ready`. Kafka is reported only by `/health/outbox`, so a Kafka outage never removes the API from rotation. |
