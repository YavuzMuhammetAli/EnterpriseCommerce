# Tenant & Merchant Integration Events

The integration events published by the Tenant & Merchant service. The conventions (envelope, topics, versioning, consumer obligations) are in [README.md](README.md).

- **Status:** **accepted** with [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) and **published** since Phase 3 S4 (2026-11-23). No consumer exists yet.
- **Design:** [PHASE_3_S4_EVENT_FLOW.md](../architecture/PHASE_3_S4_EVENT_FLOW.md).

## Topic

| Property | Value |
|---|---|
| Topic | `tenant-merchant.events` |
| Producer | `tenant-merchant` |
| Message key | The tenant ID (lowercase GUID, `D` format) |
| Ordering | Per tenant, in commit order |
| Delivery | At least once, through the transactional outbox, asynchronously after the commit |

## Events

| Event | Version | Emitted when | Status |
|---|---|---|---|
| [`TenantCreated`](#tenantcreated-v1) | 1 | A tenant's creation has committed, by trusted provisioning or by self-service onboarding | Published (S4) |

**Not published (deferred):**
- **Membership events**, until the slice of their first consumer. See [Initial owner identity](#initial-owner-identity-intentionally-excluded).
- **Tenant suspension and closure**, which arrive with Admin tenant management.
- **Invitation events.**

## TenantCreated v1

- **Schema:** [schemas/tenant-merchant/TenantCreated.v1.schema.json](schemas/tenant-merchant/TenantCreated.v1.schema.json), the payload. The message is an envelope ([schemas/envelope.v1.schema.json](schemas/envelope.v1.schema.json)) whose `payload` is this object.
- **Golden sample:** [schemas/tenant-merchant/samples/TenantCreated.v1.json](schemas/tenant-merchant/samples/TenantCreated.v1.json).

### Semantics: the complete, committed creation

`TenantCreated` states one fact: **a tenant's creation has committed, completely.** When a consumer receives it, all of these exist in Tenant & Merchant, committed together:

1. the tenant (`Tenants`), with status `Active`
2. its merchant profile (`MerchantProfiles`)
3. its initial `MerchantOwner` membership (`Memberships`), active
4. the audit record of that membership (`MembershipAuditRecords`: `OwnerProvisioned` or `SelfServiceOwnerMembershipCreated`)

How this is guaranteed:

- **Same transaction.** The event's outbox row is written in the **same SQL Server transaction** as the four records ([Transaction boundary](#transaction-boundary)). Either all five commit, or none does.
- **Asynchronous publication after commit.** The relay reads committed rows only and publishes after the commit. A consumer can therefore never see the event of an attempt that rolled back:
  - a unique conflict
  - a deadlock retry
  - a failed audit write
  - a lock timeout
- **One fact, one message.** There is no event per intermediate step: no "profile created" and no "owner added" as separate facts of the creation.
- **Read-your-writes.** A consumer that reads Tenant & Merchant's API after receiving the event finds the tenant.
- **No event without a creation.** A replayed request (the same idempotency key and request) and a refused request create nothing and emit nothing:
  - a taken slug
  - a reused key
  - the per-user limit
  - validation
- **Business time.** `occurredAtUtc` is the tenant's `CreatedAt`. It is recorded just before the commit, so it can precede the commit by the transaction's duration (milliseconds).

Both creation paths emit the event once:

| Path | Endpoint | `origin` |
|---|---|---|
| Trusted provisioning | `POST /internal/v1/tenants` | `Provisioned` |
| Self-service onboarding | `POST /api/v1/tenants` | `SelfService` |

### Transaction boundary

| Record | Table | Provisioning | Self-service |
|---|---|---|---|
| Tenant | `Tenants` | ✓ | ✓ |
| Merchant profile | `MerchantProfiles` | ✓ | ✓ |
| Initial owner membership (the active-owner trigger runs in the same transaction) | `Memberships` | ✓ | ✓ |
| Audit record | `MembershipAuditRecords` | ✓ | ✓ |
| Outbox message (`TenantCreated` v1) | `OutboxMessages` | ✓ | ✓ |
| **Transaction** | | the single `SaveChanges` of `TenantProvisioningStore.AddAsync`, which EF Core runs in one transaction | the explicit `READ COMMITTED` transaction of `TenantOnboardingGate`, under the subject's application lock |

- **Kafka is never called inside a business transaction.** The HTTP response returns after the commit and never waits for Kafka.
- The relay publishes later, asynchronously, in its own process loop.
- After delivery, it records `PublishedAt` in a separate, short statement.

### Envelope values

| Field | Value |
|---|---|
| `eventType` | `TenantCreated` |
| `eventVersion` | `1` |
| `publishedVersions` | `[1]`: `TenantCreated` is published in version 1 only |
| `producer` | `tenant-merchant` |
| `aggregateId`, `tenantId` | The tenant ID |
| `occurredAtUtc` | The tenant's creation time |
| `correlationId` | The creating request's `X-Correlation-ID` |
| `causationId` | The same value: a root message caused by an HTTP request |

### Payload

| Property | Type | Required | Rule (aligned with the creation validation) |
|---|---|---|---|
| `slug` | string | yes | `TenantSlug`: 3 to 63 of `a-z`, `0-9` and single `-`, starting and ending with a letter or digit. Immutable and platform-unique. |
| `displayName` | string | yes | `MerchantDisplayName`, exactly as stored. See [Display name](#display-name). |
| `status` | string, **open set** | yes | `Active` today. A PascalCase token of at most 32 characters (`^[A-Z][A-Za-z0-9]{0,31}$`). |
| `origin` | string, **open set** | yes | `Provisioned` or `SelfService` today. The same token rule. **Informational only.** |

### Display name

The creation endpoints accept a display name of 1 to 200 characters *after trimming*, with no control characters. They store the **trimmed** value (`MerchantDisplayName.TryCreate`). The event carries the stored value, so:

- **White space only is impossible.** Trimming such a value leaves it empty, and the request is refused with `400` before anything is written.
- The value never starts or ends with white space. White space is .NET's `char.IsWhiteSpace`: U+0009 to U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029, U+202F, U+205F and U+3000.
- It contains no control character (Unicode category Cc: U+0000 to U+001F, U+007F to U+009F). Interior spaces are allowed.
- **The schema states the same rule** as a `pattern`: first and last character neither white space nor control, no control character anywhere. A differential check over every BMP code point, alone and in three positions (253,952 cases), found no difference between the schema (ECMA-262, Ajv) and the domain rule.
- **Length.** The producer limits 200 UTF-16 code units. The schema's `maxLength` counts code points, so it is never stricter than the producer. Consumers should size storage for 200 UTF-16 code units (at most 600 bytes as UTF-8).
- **No unpaired surrogates.** System.Text.Json refuses them when reading a request and when writing a message, so none can reach the event.

### Initial owner identity: intentionally excluded

`TenantCreated` carries **no Identity subject ID**: neither the initial owner nor the self-service creator.

**Evaluation.** S4 has no consumer that needs the owner's identity:

| Plausible consumer | Does it need the initial owner in `TenantCreated`? |
|---|---|
| Catalog (merchant authorization) | No. It needs the **current** memberships and roles of every member, kept up to date. An initial owner is a snapshot that goes stale on removal, suspension or a later ownership transfer, and a consumer authorizing from it would be wrong. |
| Notification (a welcome email) | Not designed. It would also need a delivery address, which Identity owns. The design of that flow decides what it needs. |
| Admin (tenant overview) | No. It can read the owner from Tenant & Merchant's internal API, authorized and audited. |

**Decision.**
- Membership identity is excluded, not added for convenience. A subject ID is pseudonymous personal data. Once published, it stays in the topic for the retention period, beyond Tenant & Merchant's control.
- When a consumer justifies it, **membership events** are introduced as their own contract:
  - the complete lifecycle set (created, role changed, suspended, reactivated, removed) and its ordering guarantees
  - designed with that consumer
  - the owner membership's creation event is emitted in the same transaction as `TenantCreated`, keyed by the same tenant ID, so it is ordered after it

  `TenantCreated` v1 does not change for this.

**Also excluded** (audit and idempotency data that stay in Tenant & Merchant):
- the provisioning client ID
- the idempotency key and the request hash
- the actor reference

### Unknown `status` and `origin` values (consumer obligations)

`status` and `origin` are **open sets**. A value that matches the token rule but is unknown to a consumer is a **valid message**, not a malformed one. New values arrive within v1 as the platform evolves, for example a pending status with merchant applications.

| Rule | Behaviour |
|---|---|
| **Never map an unknown value to a known one** | Do not treat it as `Active`, a default, or the nearest match. Do not drop the property. |
| **Fail closed** | The tenant is recorded with the raw value preserved. Every capability that requires a known state is denied until the consumer understands the value. For example, a projection that allows selling only for `Active` does not allow it. A stateless reaction (such as an email for active tenants) does nothing. |
| **`origin` is informational** | No consumer derives authorization or business permissions from it. An unknown origin is stored and logged, and otherwise has no effect. |
| **Log** | One Warning per message, with `eventId`, `eventType`, the property name and the value. The value is a bounded token, safe to log. Count it in a metric tagged by property name only. |
| **No retry** | The outcome is deterministic, so a retry cannot change it. The message is processed (the inbox row and the projection with the raw value) and its offset committed. |
| **No dead-letter topic** | Dead-lettering would remove a valid fact from the projection and require a manual replay. Because the raw value is kept, an upgraded consumer re-evaluates its stored rows without a replay. |
| **Dead-letter topic only for invalid messages** | A message that fails the envelope or payload schema: a missing required property, a wrong type, a value outside the token rule such as `""` or `active`, or headers disagreeing with the body. This is a producer defect, quarantined in `<consumer-group>.dlt` with the reason. |
| **Transient failures** | Retried with bounded backoff, blocking the partition (for example the consumer's database is down). This is unrelated to the value. |

**Producer obligations:**
- A new value is added to this contract and its changelog before it is first emitted.
- A new value never changes the meaning of an existing one.
- If failing closed on the new value would be unsafe for a documented consumer, the change is breaking and needs a new version.

### Evolution of this event

- **Optional properties are added within v1.** Example: a later `websiteUrl`.
  1. It is added to the schema's `properties`, never to `required`.
  2. It gets a changelog entry and a new golden sample (`TenantCreated.v1.<yyyy-mm-dd>.json`). The existing sample is never edited and keeps validating.
- **Existing consumers are unaffected:**
  - they ignore unknown properties (tolerant reader)
  - a consumer that validates against an older copy of the v1 schema still accepts the new messages, because every schema sets `additionalProperties: true`
- **New consumers** treat the property as possibly absent: messages produced before the change, and replays, lack it.
- **Breaking changes need `TenantCreated` v2.** Breaking means:
  - removing, renaming or retyping a property
  - making a property required
  - narrowing a value range
  - changing a meaning
  - a new open-set value that consumers cannot safely fail closed on

  The procedure is in [README.md](README.md#versioning-and-compatibility): dual publication with the same `eventId`, then the retirement of v1.

## Changelog

| Date | Change |
|---|---|
| 2026-11-23 | `TenantCreated` v1 proposed (ADR-023). It replaces the provisional name `TenantProvisioned`. |
| 2026-11-23 | Before the first commit: the envelope gains `publishedVersions` (`[1]` for this event), and consumers select one version per fact through their declared preferred version ([README.md](README.md#version-selection)). |
| 2026-11-23 | Accepted with ADR-023. Final clarification: `eventId` identifies the logical fact; `eventVersion` is the payload contract version; per-fact deduplication and replay ([README.md](README.md#consumer-processing)). |
| 2026-11-23 | Published: Phase 3 S4 implements the outbox and the relay. The producer's messages are tested against this schema, the envelope schema and the golden sample. |
| 2026-11-23 | Review: the committed-creation semantics, the transaction boundary, the exclusion of membership identity, consumer handling of unknown open-set values, a `displayName` pattern aligned with `MerchantDisplayName`, and the token rule for `status` and `origin`. |
