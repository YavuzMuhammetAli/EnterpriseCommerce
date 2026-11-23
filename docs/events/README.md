# Integration Events

The integration event contracts of EnterpriseCommerce and the conventions every contract follows.

- **Status:** the conventions below are **accepted** in [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) (Phase 3 S4, 2026-11-23) and binding for every producer and consumer.
- **Published:** `TenantCreated` v1, by Tenant & Merchant since Phase 3 S4 ([PHASE_3_S4_EVENT_FLOW.md](../architecture/PHASE_3_S4_EVENT_FLOW.md)). **No consumer exists yet**: the consumer rules below are accepted and bind the first consumer's slice.
- Kafka runs locally in KRaft mode with automatic topic creation disabled; topics come from [deploy/docker/kafka/topics.txt](../../deploy/docker/kafka/topics.txt).

The accepted rules these conventions implement ([ADR-005](../decisions/ADR-005-kafka-for-asynchronous-communication.md), [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md), [ADR-013](../decisions/ADR-013-saga-based-workflow-coordination.md), [ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)):

- Integration events state facts in the past tense. Commands are imperative and have a single owner.
- Contracts are versioned, and consumers are idempotent.
- Every message carries correlation and causation identifiers, and tenant context where relevant.
- Messages never contain sensitive data (credentials, tokens or payment instrument data).
- Messages are published through the transactional outbox, never directly from a business transaction.

## Contracts

| Producer | Contract | Topic | Events |
|---|---|---|---|
| Tenant & Merchant | [TENANT_MERCHANT_EVENTS.md](TENANT_MERCHANT_EVENTS.md) | `tenant-merchant.events` | `TenantCreated` v1 (published since S4) |
| Catalog | none yet | none (`catalog.events` when introduced) | **No events in Phase 4.** The standalone MongoDB has no multi-document transactions for a transactional outbox, and no consumer exists. Prerequisites: a replica set in Compose and CI, a MongoDB outbox design under ADR-023, and the first consumer ([ADR-024](../decisions/ADR-024-catalog-service-design.md) section 13). |

Schemas and golden samples live in [schemas/](schemas/):

- [schemas/envelope.v1.schema.json](schemas/envelope.v1.schema.json): the envelope shared by every message.
- `schemas/<producer>/<EventType>.v<N>.schema.json`: the payload schema of one event version.
- `schemas/<producer>/samples/<EventType>.v<N>.json`: a complete golden message (envelope and payload).

## Topics, keys and consumer groups

| Item | Convention | Example |
|---|---|---|
| Integration events | One topic per producing service: `<service>.events`. The service's event types share it. | `tenant-merchant.events` |
| Commands (future) | `<recipient-service>.commands`, owned by the recipient | `inventory.commands` |
| Consumer group | `<consumer-service>.<purpose>`, owned by the consumer | `catalog.tenant-projection` |
| Dead-letter topic | `<consumer-group>.dlt`, owned by the consumer | `catalog.tenant-projection.dlt` |
| Characters | Lowercase letters, digits, `.` and `-`. Never `_`. | |
| Message key | The tenant ID (lowercase GUID, `D` format) for every tenant-scoped message | |

- **Ordering:** the messages of one tenant arrive in commit order, within one partition. There is no order across tenants.
- **Topic creation is explicit.**
  - Topics are declared in a committed manifest and created by the Compose setup (locally), the test fixtures (tests), or infrastructure as code (deployed environments).
  - An application never creates a topic.
- **Locally and in CI:** 3 partitions, replication factor 1, `cleanup.policy=delete`, `retention.ms` 7 days. Production values are set in the deployment phase.

## Envelope

Every message body is a self-contained JSON envelope ([schemas/envelope.v1.schema.json](schemas/envelope.v1.schema.json)). The same bytes are stored in the producer's outbox and published.

| Field | Type | Rule |
|---|---|---|
| `eventId` | GUID (version 7) | **The identity of the logical business fact**, not of an individual serialized message. **The consumers' deduplication key.** Every message that states the same fact carries the same `eventId`: redeliveries, and the versions of a dual publication. A message is identified by (`eventId`, `eventVersion`). |
| `eventType` | string | Past-tense PascalCase, unique within the producer |
| `eventVersion` | integer ≥ 1 | **The version of the payload contract** of (`producer`, `eventType`), its major version. It versions neither the envelope nor the aggregate's state. |
| `publishedVersions` | array of integers ≥ 1, unique, non-empty, containing `eventVersion` | **The versions in which the producer publishes this fact**, identical in every message of the fact. `[1]` for a fact published in one version, `[1, 2]` during a dual publication. Consumers use it to select one version deterministically ([Version selection](#version-selection)). |
| `producer` | string | The producing service (`tenant-merchant`) |
| `aggregateId` | GUID | The aggregate the fact is about |
| `tenantId` | GUID or `null` | Required for every tenant-scoped message |
| `occurredAtUtc` | timestamp | The business time of the fact, not the publish time |
| `correlationId` | string, 1 to 64 of `A-Z a-z 0-9 - _ . :` | The flow's correlation ID (`X-Correlation-ID` for HTTP-initiated work) |
| `causationId` | string, same rules | The `eventId` of the causing message. For a message caused directly by an HTTP request, its correlation ID, so a root message has `causationId == correlationId`. |
| `payload` | object | The event-specific data |

(`producer`, `eventType`, `eventVersion`) identifies the payload schema.

**Kafka headers** repeat values for routing and tracing:
- `event-id`, `event-type`, `event-version` and `correlation-id`
- `content-type: application/json`

The body is authoritative. A message whose headers disagree with its body is malformed.

**Encoding:**
- UTF-8 JSON
- property names in camelCase
- timestamps in ISO-8601 UTC with `Z`
- GUIDs lowercase in `D` format
- enums as strings
- at most 64 KiB per message

**Never in a message:**
- credentials, tokens and secrets
- payment instrument data
- email addresses
- idempotency keys and request hashes
- client IDs and actor references
- Identity subject IDs, unless an accepted contract explicitly needs them

## Versioning and compatibility

**Two schemas, validated in two steps.** A message is valid when both of these hold:

1. **The envelope** validates against [envelope.v1.schema.json](schemas/envelope.v1.schema.json). This schema is shared by every message of every producer. It requires `payload` to be an object and says nothing more about it.
2. **The payload** validates against the schema selected by (`producer`, `eventType`, `eventVersion`), at `schemas/<producer>/<eventType>.v<eventVersion>.schema.json`.

The two are versioned independently:

- **`eventVersion` versions the payload only.**
- **The envelope version is not carried in messages.** The envelope changes only additively, and a breaking envelope change needs a new topic.
- A payload schema never repeats envelope fields.
- Every schema sets `additionalProperties: true`.

**Rules:**

1. `eventVersion` is a major version. **Within a version, the only allowed change is adding an optional property**, or a documented value of an open set.
2. **Breaking changes need a new version.** They are:
   - removing, renaming or retyping a property
   - making a property required
   - narrowing a range or pattern
   - changing a meaning
   - an open-set value that consumers cannot safely fail closed on
3. Consumers are **tolerant readers**. They ignore unknown properties. For an open-set property they handle unknown values: never map them to a known value, preserve the raw value, fail closed, log. The details are in each producer's contract.
4. A published schema or sample file is never edited except to add an optional property. Its golden samples stay valid forever.

**Adding an optional property (within `vN`):**

1. The producer adds it to the payload schema's `properties`, never to `required`.
2. The producer adds a changelog entry and a new golden sample. The existing samples are not edited and keep validating.
3. Existing consumers are unaffected. They ignore the property, and a consumer that validates against its older copy of the schema still accepts the message, because of `additionalProperties: true`.
4. Consumers that start using the property treat it as possibly absent. Older messages and replays lack it.

**A breaking change (`vN+1`):**

1. A new schema file `<eventType>.v<N+1>.schema.json` and a golden sample are committed. The `vN` files stay.
2. **Dual publication.**
   - For the migration period, the producer writes both versions of the fact in the same transaction: two outbox rows with the **same `eventId`**, one per `eventVersion`, on the same topic and with the same key.
   - The pair (`eventId`, `eventVersion`) is unique per message, and the producer's outbox enforces it.
   - The two messages state **one** fact, and both carry `publishedVersions: [N, N+1]`.
   - **Producer obligation:** every message of one fact carries the **identical** `publishedVersions` list, with the same values in the same ascending order. The producer computes the list once per fact, before it writes any of the fact's outbox rows, and writes it into each version's envelope. A consumer cannot compare messages, so it relies on this rule.
   - Their order within the partition is not part of the contract.
3. **Consumers** follow [Consumer processing](#consumer-processing):
   - A consumer applies the fact from **one selected version**: its preferred version when that version is published for the fact, otherwise its highest other supported version.
   - The selection depends only on the consumer's declaration and `publishedVersions`, never on arrival order.
   - Every other version is acknowledged without a processed-event record.
   - The fact is therefore applied at most once, and from the same version whatever the arrival order.
4. Once the documented consumers have migrated, `vN` is retired with a changelog entry. Publication stops, and new facts carry `publishedVersions: [N+1]`. The `vN` files stay as history.

**CI checks** (implemented with the first producer, Phase 3 S4, in `TenantMerchant.UnitTests`):
- the generated schema of each contract type matches the committed schema (structure: properties, types, required)
- every golden sample still deserializes into the current contract without data loss
- the producer's real output has the shape of the sample, and validates against the envelope and payload schemas (Corvus.Json.Validator, test-only)

## Delivery and consumer obligations

- **Delivery is at least once.**
  - A message can arrive more than once, always with the same `eventId` and the same bytes.
  - During a dual publication, one fact arrives as two messages with the same `eventId`.
  - Exactly-once delivery is not provided.
- **Consumers:**
  - deduplicate per logical fact, by `eventId` ([Consumer processing](#consumer-processing))
  - process each partition in order
  - commit offsets only after processing
  - retry transient failures with bounded backoff
  - send malformed or permanently failing messages to their dead-letter topic
- **An unsupported `eventType` or `eventVersion`** is acknowledged (its offset committed) and logged. It is never treated as a failure, and it **never creates a processed-event record**.
- **A supported version that is not selected** for its fact ([Version selection](#version-selection)) is acknowledged the same way, without a processed-event record.
- **An unknown value of an open-set property** makes a valid message. The consumer:
  - processes it with the raw value preserved and fails closed
  - logs it
  - does not retry it or dead-letter it

  Only schema-invalid messages go to the dead-letter topic.
- **`tenantId`** is validated, and every effect is scoped to it (ADR-014).
- **A new consumer bootstraps** through a designed replay or backfill, not by assuming unlimited topic retention.

## Consumer processing

These rules are binding for every consumer. They are implemented with the first consumer; S4 implements none.

**Two different acknowledgements.** Keep them apart:

| | What it is | Written when |
|---|---|---|
| **Offset commit** (Kafka) | The consumer group's position in a partition: "this message needs no further delivery to this group" | After every message is finished: processed, skipped as a duplicate, skipped as not selected, unsupported, or dead-lettered |
| **Processed-event record** (the consumer's database) | "The fact `eventId` has been applied by this consumer" | **Only** when the consumer applied the fact from its **selected** version, in the same transaction as the effect |

**The processed-event record:**
- A table in the consumer's own database (the "inbox").
- Primary key (`Consumer`, `EventId`), where `Consumer` is the consumer group, for example `catalog.tenant-projection`.
- Columns `EventVersion` (the selected version that was applied, for diagnostics only, never part of the key) and `ProcessedAt`.

### Version selection

**Each consumer declares, per event type:**
- the versions it **supports**, `S`
- **exactly one preferred version** `p` in `S`

This is part of the consumer's code and configuration, changed only by a deployment, and documented with the consumer.

**For every message of a fact,** with `P` = its `publishedVersions`:

1. If `S` and `P` have no version in common, the message is **unsupported**. So is every other message of that fact: the approved unsupported-version behaviour applies.
2. Otherwise the fact's **selected version** is:
   - `p`, if `p` is in `P`
   - otherwise, the highest version in both `S` and `P`. This **fallback** covers facts published only in a non-preferred version, for example v1 facts from before a dual publication, or v2 facts after the retirement of a preferred v1.
3. **A message whose `eventVersion` is the selected version is processed** (below). It creates the processed-event record (`consumer group`, `eventId`).
4. **A message of a supported version that is not the selected one** is acknowledged and logged (Debug, with a counter), **without a processed-event record**. It is skipped whether it arrives before or after the selected version.
5. **A message whose `eventVersion` is not in its own `publishedVersions`** is malformed and goes to the dead-letter topic.

**Determinism.**
- The selected version depends only on (`S`, `p`, `P`), and `P` is identical in every message of the fact.
- A consumer therefore applies a fact **from the same version in every arrival order**, on every redelivery and on every replay.
  - **The preferred version arrives first:** it is applied and recorded. The later version is not selected and is skipped; the record would also deduplicate it.
  - **A non-preferred version arrives first:** it is skipped without a record. The preferred version is applied when it arrives.

**Why `publishedVersions` is needed.** Without it, a consumer that prefers v2 cannot tell, from a v1 message, whether a v2 copy of the same fact will follow:
- If it always skipped v1, it would never apply facts published only as v1.
- If it applied v1 when v2 had not yet arrived, the result would depend on arrival order again.

**Processing one message:**

1. Read and validate the envelope. A schema-invalid message goes to the dead-letter topic, then the offset is committed.
2. Determine the selected version ([Version selection](#version-selection)).
   - If the message is unsupported, or its version is supported but not selected: log it, write **no** processed-event record, and commit the offset.
   - If its `eventVersion` is not in its `publishedVersions`: dead-letter it, then commit the offset.
3. Begin a transaction in the consumer's database.
   - Insert the processed-event record (`Consumer`, `eventId`).
   - If it already exists, the fact was applied before: by an earlier delivery of this message, or under an earlier selection (see [Changing the preferred version](#changing-the-preferred-version)). Roll back, record a duplicate metric, and commit the offset.
4. Validate the payload and the tenant. Apply the effect. Commit the transaction.
5. Commit the offset.

   A crash between step 4 and step 5 redelivers the message, and step 3 then skips it. A crash before step 4's commit leaves no record, so the message is processed again.

**The guarantee:**
- **A logical fact is applied at most once per consumer, even when it is delivered several times or published in several versions.**
  - The unique key of the processed-event record makes this hold under concurrent deliveries as well, for example during a partition rebalance.
- Together with at-least-once delivery and rollback on failure, a fact whose selected version is delivered is applied once, as long as its processed-event record is retained.
- **It is applied from its selected version, independent of arrival order.**
- This holds **per consumer**. It is not exactly-once delivery, and it does not cover side effects outside the consumer's transaction (see below).

**Version support cases** (every cell holds in both arrival orders):

| Consumer: supported `S`, preferred `p` | Dual-published fact, `P = [1, 2]` | Fact only as v1, `P = [1]` | Fact only as v2, `P = [2]` |
|---|---|---|---|
| `S = {1}`, `p = 1` | Applied from v1. v2 unsupported, no record. | Applied from v1 | Unsupported, no record (applied by a replay after an upgrade) |
| `S = {2}`, `p = 2` | Applied from v2. v1 unsupported, no record. | Unsupported, no record (as above) | Applied from v2 |
| `S = {1, 2}`, `p = 2` | **Applied from v2.** v1 not selected, no record. | Applied from v1 (fallback) | Applied from v2 |
| `S = {1, 2}`, `p = 1` | **Applied from v1.** v2 not selected, no record. | Applied from v1 | Applied from v2 (fallback) |

**Side effects outside the transaction** (an email, a call to another service) cannot share the processed-event record's transaction. Such a consumer records the fact first and performs the effect idempotently, for example through its own outbox, or with the `eventId` as the idempotency key of the call.

### Replay semantics

| Operation | What it does | What it does not do |
|---|---|---|
| **Gap replay**: reset the group's offsets to an earlier point, keeping the projection and its processed-event records | **Recovers facts that were previously skipped and not recorded**: unsupported, or supported but not selected, at the time. They are selected under the current declaration and applied. | It does **not** upgrade facts already applied and recorded. Their records make the replay skip every version of them, so their data stays as derived from the version selected at the time. |
| **Rebuild**: in one transaction, delete the projection's data **and** its processed-event records, then replay from the earliest retained offset | **Migrates the whole projection** to the current declaration, including a newly preferred version. Every retained fact is applied once, from its currently selected version. | It does not reach facts older than the topic's retention; they need the backfill design. Deleting only the projection, or only the records, is never allowed: the first skips every fact, the second applies facts twice. |

A projection that must be **fully** migrated to a newly preferred version therefore requires a **rebuild**. A gap replay is enough when only previously skipped facts need to be applied.

### Replay after a consumer upgrade

A consumer that gains support for a version (or an event type) it did not support may replay the messages it acknowledged without processing.

**Why it works:**
- Unsupported and not-selected messages never created a processed-event record. On replay, those facts are selected again under the new declaration, and applied.
- Facts already applied, from whichever version was selected then, are skipped by their existing records.

| Before the upgrade | Upgrade | On replay |
|---|---|---|
| Supported v1 only | Gains v2 (with `p = 1` or `p = 2`) | Facts applied from v1 (including dual-published ones) are skipped and keep their v1-derived data. Facts published only as v2 are applied. |
| Supported v2 only | Gains v1 | Facts published only as v1 (before the dual publication) are applied. Dual-published facts already applied from v2 are skipped. |
| Did not support the event type | Gains it | Every retained fact of that type is applied once |

**Procedure** (run by the consumer's owner; the producer is not involved):

1. Deploy the upgraded consumer, and stop its group: an offset reset needs a group without active members.
2. Reset the group's offsets to the start of the gap, for example `kafka-consumer-groups.sh --group <group> --topic <topic> --reset-offsets --to-datetime <start> --execute`, or `--to-earliest`.
3. Start the consumer. Processed facts are skipped by their records; the rest are applied.

**Implications:**
- **Retention of processed-event records.** They must be kept at least as long as the topic's retention, plus the longest planned replay window. A record deleted earlier lets a replay apply its fact a second time. Their cleanup is by `ProcessedAt`, and never by version: a record represents the fact, not the version that delivered it.
- **The replay window is bounded by topic retention.** Older facts need the backfill design.
- **Order.** A replayed fact is applied *after* later facts that were applied the first time round.
  - A gap replay is correct only when the effects of the newly supported messages do not depend on that order. Examples: they set data that only this event type owns, or the consumer checks a per-aggregate order (a sequence, deferred to the membership-event design).
  - Otherwise, use a **rebuild**:
    1. in one transaction, delete the projection's data **and** its processed-event records
    2. reset the offsets to the earliest retained message
    3. replay

    Deleting only the projection would make the replay skip every fact. Deleting only the records would apply facts twice.
- **Side effects of old facts.** A replay applies facts that may be old. A consumer with externally visible side effects decides whether a replayed old fact still warrants the side effect, for example no welcome email for a tenant created months ago. It must not treat the fact as new by default.

### Changing the preferred version

The preferred version is a consumer deployment setting. Changing it (for example from `p = 1` to `p = 2` once v2 is dual-published) affects only facts **not yet applied**.

- **Facts already applied keep their processed-event records and their data**, derived from the version selected at the time.
  - On a later replay, the newly selected version of such a fact is deduplicated: a preference change never applies a fact twice.
  - It also never "upgrades" an existing fact's data to the new version.
- **Facts not yet applied** are selected under the new preference when they are consumed or replayed.
- **The switch point.** Before the change, the consumer may have skipped the first-arriving, then-unselected version of a fact whose other version it had not yet consumed. After the change that other version may no longer be selected either, and the fact would be applied from neither.
  - **Therefore every preference change is followed by a gap replay**: stop the group, change `p`, reset the offsets to a point before the last committed offset, then start. Go back at least the producer's dual-publication window: the two messages of a fact are written in one transaction and published consecutively, so minutes suffice, but take a generous margin.
  - Facts already applied are deduplicated, and facts skipped at the switch point are applied.
- **To derive every retained fact from the new preferred version, rebuild** (see above): in one transaction, delete the projection's data and its processed-event records, reset the offsets to the earliest retained message, and replay.
  - Each fact is then applied from its selected version under the new declaration, with the fallback for facts published in one version only.
  - Facts older than the topic's retention need the backfill design.
- **Before the producer retires a version**, every consumer must support the remaining version. The fallback then selects it for the new facts, which carry `publishedVersions: [N+1]`, even if the consumer still prefers `N`.
