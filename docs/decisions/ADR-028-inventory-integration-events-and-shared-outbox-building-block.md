# ADR-028: Inventory Integration Events and the Shared Outbox Building Block

- **Status:** Accepted
- **Date:** 2026-11-24
- **Phase:** 5 (Inventory)
- **Related:**
  - [ADR-005](ADR-005-kafka-for-asynchronous-communication.md) and [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md) (Kafka, the outbox direction)
  - [ADR-013](ADR-013-saga-based-workflow-coordination.md) (rule 7: an event for reservation expiration)
  - [ADR-023](ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) (the outbox implementation; this ADR applies its section 8 and amends its section 2 for Inventory)
  - [ADR-026](ADR-026-inventory-service-design.md) (the facts these events state)
- **Amends:** ADR-023 section 2, the ordering guarantee, **for the Inventory producer only** (section 3 below). ADR-023's status line points here.
- **Applies:** ADR-023 section 8: the outbox code moves to `EnterpriseCommerce.BuildingBlocks.Messaging` now that a second service publishes.
- **Contracts:** [INVENTORY_EVENTS.md](../events/INVENTORY_EVENTS.md). **Design and evidence:** [PHASE_5_INVENTORY.md](../architecture/PHASE_5_INVENTORY.md).

## Context

Inventory's balance and reservation changes are facts that other services will need:

- Order will react to expired reservations (ADR-013 rule 7).
- A storefront availability projection will follow stock levels.

Inventory is SQL Server-backed, like Tenant & Merchant, so ADR-023's SQL Server outbox applies as designed.

Two things in ADR-023 need a decision now:

1. **Section 8** says the outbox, envelope and relay code "moves to `EnterpriseCommerce.BuildingBlocks.Messaging` when a second service publishes". Inventory is the second publisher. All of that code is `internal` to `TenantMerchant.Infrastructure`, and the architecture tests allow Confluent.Kafka only there.
2. **Section 2** guarantees order "per tenant, in commit order". That guarantee "rests on two properties: the producing service serializes the writes of one tenant, and the relay publishes sequentially". Inventory deliberately does **not** serialize a tenant's writes: independent items must not contend (ADR-026 section 9). For two concurrent transactions of one tenant that touch different aggregates, the relay publishes in `Sequence` (identity) order. An identity value is allocated at insert, not at commit, so this order can differ from commit order.

## Decision

### 1. Topic, producer and key

- The topic is **`inventory.events`** (ADR-023 section 2), created explicitly from `deploy/docker/kafka/topics.txt`. Locally it has 3 partitions, replication factor 1 and a retention of 7 days.
- The producer is **`inventory`**.
- **The key is the tenant ID**, as ADR-023 requires for tenant-scoped messages. A tenant's messages share one partition.

### 2. The events (all version 1, `publishedVersions: [1]`)

| Event | Aggregate | Emitted when |
|---|---|---|
| `StockLevelChanged` | inventory item | Every movement: initial stock, an adjustment, and per line a reservation, release, expiry or commit. One event per movement, in the movement's transaction. |
| `ReservationCreated` | reservation | A reservation is created, with all its lines. Not on an idempotent replay. |
| `ReservationCommitted` | reservation | `Active` → `Committed` |
| `ReservationReleased` | reservation | `Active` → `Released` by an explicit release |
| `ReservationExpired` | reservation | `Active` → `Expired`, by the worker or lazily (ADR-026 section 7) |

- **Expiry is its own type**, not a release with a reason. Order must react to it differently: the checkout lost its stock (ADR-013 rule 7).
- **`StockLevelChanged` payload:**
  - `movementId`
  - `movementType` (open set: `InitialStock`, `Adjustment`, `Reservation`, `ReservationRelease`, `ReservationExpiry`, `ReservationCommit`)
  - `adjustmentReason` (open set, or `null`)
  - `productId`, `variantId` and `sku` (the snapshot)
  - `onHandDelta` and `reservedDelta`
  - `onHand`, `reserved` and `available` after the change
  - `itemVersion`
  - `reservationId`, or `null`
- **Reservation payloads:**
  - `status` (open set)
  - `lines`: `[{ inventoryItemId, productId, variantId, quantity }]`, between 1 and 50
  - `expiresAtUtc`
  - `reference`, or `null`
  - `reservationVersion`
- **Excluded,** per ADR-023 section 4:
  - the service client that owns a reservation
  - the merchant subject or any actor reference
  - idempotency keys and request hashes
- **The size bound.** At most 50 lines keeps every envelope far below the 64 KiB limit. The serializer still checks the limit before the write.

### 3. Ordering guarantee for Inventory (amends ADR-023 section 2)

- **Per aggregate, in commit order.** Every write locks its aggregate's row (the item, or the reservation) **before** the outbox rows are inserted, in the same transaction (ADR-026 section 9). A second transaction on the same aggregate can therefore insert only after the first committed, which gives it a higher `Sequence`. The relay publishes sequentially in `Sequence` order, so one aggregate's events arrive in commit order on the tenant's partition.
- **Every payload carries the aggregate's version:** `itemVersion` for items, and `reservationVersion` for reservations. A consumer can verify the order or ignore an older state.
- **Across aggregates of one tenant, order is not commit order.** Two concurrent transactions on different items can commit in one order and be published in the other. No consumer may rely on cross-aggregate order. Within one transaction, the order of its events is not part of the contract either.
- ADR-023's per-tenant guarantee **stays unchanged for Tenant & Merchant**, which serializes per tenant.

### 4. The outbox code moves to `EnterpriseCommerce.BuildingBlocks.Messaging` (applies ADR-023 section 8)

- **Contents:** technical code only, with no business type:
  - the envelope, the fact and version types, and the serializer
  - the `OutboxMessage` entity and its EF Core model mapping (`OutboxMessages`)
  - the relay with its ports
  - the SQL Server leadership and store
  - the Confluent.Kafka publisher
  - the options and their validation
  - the `outbox` health check
  - the relay state and the meters
- **Per-service parameters,** in an `OutboxRelayProfile`:
  - the settings sections
  - the default topic
  - the Kafka `client.id` and the lock session's application name
  - the meter name

  Each service keeps its own domain-event mapper, its own `DbContext` hook and its own contracts. **No service contract is shared.**
- **Usage rule:** only `*.Infrastructure` projects may reference the block. The architecture tests allow Confluent.Kafka only in the block, and the outbox and envelope types only in the block and in the publishing services' Infrastructure projects.
- **Tenant & Merchant moves with unchanged behaviour:**
  - the same table, so its model and migration are unchanged
  - the same settings sections, topic, `client.id`, lock resource and application name, and meter name
  - the same log event IDs (3300 to 3349, now owned by the block)

  Its existing unit, SQL Server and Kafka tests are the regression evidence.
- **Store abstraction:** SQL Server only. The MongoDB outbox for Catalog (ADR-024 section 13) will add its own store when it is designed.

### 5. Relay behaviour for Inventory

Unchanged from ADR-023 sections 5 and 6:

- one leader per service (a session application lock)
- sequential, acknowledged publishing (`acks=all`, idempotent producer)
- durable attempts and backoff on the row
- a halt instead of a skip for a poison row
- cleanup after 7 days
- graceful shutdown
- `/health/outbox` outside readiness
- Outside Development, startup is refused without mutual TLS.

No consumer and no inbox are implemented: no Inventory use case needs one in Phase 5.

## Consequences

- **Positive:** one reviewed outbox implementation serves both SQL Server publishers.
- **Positive:** Inventory's state changes reach Kafka with no lost or phantom messages, and the expiry event exists for Order.
- **Positive:** independent items stay concurrent, and consumers can still order an aggregate's facts by their version.
- **Negative:** consumers cannot rely on cross-aggregate order within a tenant. That is a weaker guarantee than Tenant & Merchant's, documented per producer.
- **Negative:** `StockLevelChanged` is emitted once per line of every reservation transition, so event volume grows with reservation traffic. The single sequential leader bounds publication throughput (ADR-023 Consequences). The local measurements are in PHASE_5_INVENTORY.md.
- **Negative:** a change to the block affects both services. Their test suites both run in CI.

## Alternatives considered

- **Copying the outbox into Inventory:** contradicts ADR-023 section 8, and doubles every future fix. Rejected.
- **Serializing every write of a tenant** to keep ADR-023's per-tenant order: it serializes a merchant's independent items. Rejected (ADR-026 section 9).
- **The inventory item ID as the message key:** it gives order per item without the amendment, but breaks ADR-023's tenant-key rule and spreads a tenant's reservation events over partitions. Rejected.
- **One `ReservationEnded` event with a reason:** Order must handle expiry distinctly, and a reason field is an open set it would have to interpret. Rejected in favour of explicit types.
- **No `StockLevelChanged`, only reservation and adjustment events:** consumers would have to compute balances. The event with after-values is self-contained. Rejected.
- **A consumer and inbox now:** no Inventory use case consumes events in Phase 5. Rejected.
