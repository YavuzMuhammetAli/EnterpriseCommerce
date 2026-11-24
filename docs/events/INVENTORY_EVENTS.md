# Inventory Integration Events

The integration events published by the Inventory service. The conventions (envelope, topics, versioning, consumer obligations) are in [README.md](README.md).

- **Status:** **accepted** with [ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) and **published** since Phase 5 (2026-11-24). No consumer exists yet; Order is expected to consume the reservation events, and a storefront availability projection `StockLevelChanged`.
- **Design:** [PHASE_5_INVENTORY.md](../architecture/PHASE_5_INVENTORY.md). **Facts:** [ADR-026](../decisions/ADR-026-inventory-service-design.md).

## Topic

| Property | Value |
|---|---|
| Topic | `inventory.events` |
| Producer | `inventory` |
| Message key | The tenant ID (lowercase GUID, `D` format) |
| Ordering | **Per aggregate** (an inventory item or a reservation), in commit order. **Not** across the aggregates of one tenant (ADR-028 section 3). |
| Delivery | At least once, through the transactional outbox, asynchronously after the commit |

The ordering guarantee is weaker than Tenant & Merchant's per-tenant order, deliberately: Inventory does not serialize a tenant's writes, so that independent items do not contend. Every payload carries its aggregate's version (`itemVersion`, `reservationVersion`), so a consumer can verify the order of one aggregate's facts or ignore an older state. Within one transaction, the order of its events is not part of the contract either.

## Events

| Event | Version | Aggregate (`aggregateId`) | Emitted when |
|---|---|---|---|
| [`StockLevelChanged`](#stocklevelchanged-v1) | 1 | inventory item | Every movement of an item's balance, in its transaction |
| [`ReservationCreated`](#reservation-events-v1) | 1 | reservation | A reservation was placed |
| [`ReservationCommitted`](#reservation-events-v1) | 1 | reservation | An active reservation was committed |
| [`ReservationReleased`](#reservation-events-v1) | 1 | reservation | An active reservation was released by its owner |
| [`ReservationExpired`](#reservation-events-v1) | 1 | reservation | An active reservation expired, by the worker or lazily (ADR-013 rule 7) |

**Every event is published in version 1 only** (`publishedVersions: [1]`). Replays and refused requests emit nothing: an idempotent replay of a reservation or an adjustment, a repeated commit or release, and every request refused for insufficient stock, an unknown variant or a terminal state.

**Never in a message** (ADR-023 section 4): the service client that owns a reservation, the merchant subject or any other actor reference, idempotency keys and request hashes.

## StockLevelChanged v1

**Semantics.** One committed movement of one item's balance (ADR-026 section 5): the initial stock, a merchant adjustment, or, for one reservation line, a reservation, release, expiry or commit.

- A reservation with *n* lines emits *n* `StockLevelChanged` events and one reservation event, all in its transaction.
- **The movement is the fact.** The envelope's `eventId` equals the payload's `movementId`, a version 7 GUID.
- **Self-contained.** The payload states the balance after the movement. A consumer projecting availability needs no other source.

Schema: [schemas/inventory/StockLevelChanged.v1.schema.json](schemas/inventory/StockLevelChanged.v1.schema.json). Sample: [samples/StockLevelChanged.v1.json](schemas/inventory/samples/StockLevelChanged.v1.json).

| Property | Meaning |
|---|---|
| `movementId` | The movement, equal to `eventId` |
| `movementType` | Open set: `InitialStock`, `Adjustment`, `Reservation`, `ReservationRelease`, `ReservationExpiry`, `ReservationCommit` |
| `adjustmentReason` | Open set or `null`: `Receipt`, `Return`, `Damage`, `Loss`, `Correction`, `CycleCount` for an `Adjustment` |
| `productId`, `variantId` | Catalog's identity of the item, verified at the item's creation (ADR-027) |
| `sku` | The SKU snapshot from the item's creation. Informational: Catalog owns the SKU, and the snapshot can be stale (ADR-026 section 11). |
| `onHandDelta`, `reservedDelta` | The changes made by the movement |
| `onHand`, `reserved`, `available` | The balance after the movement: `available = onHand − reserved`, and `0 ≤ reserved ≤ onHand` |
| `itemVersion` | 1 for the initial stock, then increased by exactly 1 per movement |
| `reservationId` | The reservation of a reservation movement, otherwise `null` |

## Reservation events v1

**Semantics.** The four reservation events state the lifecycle of ADR-026 section 6:

- `ReservationCreated` for the placement
- exactly one terminal event: `ReservationCommitted`, `ReservationReleased` or `ReservationExpired`

A reservation never emits two terminal events: terminal states are final.

- **`ReservationExpired` is its own type.** For Order, an expiry means the checkout lost its stock, which it must handle differently from its own release.
- **Lazy expiry.** A commit or release of an overdue reservation expires it, and emits `ReservationExpired`, not the requested event (ADR-026 section 7).

The four share one payload shape. Schemas: [ReservationCreated](schemas/inventory/ReservationCreated.v1.schema.json), [ReservationCommitted](schemas/inventory/ReservationCommitted.v1.schema.json), [ReservationReleased](schemas/inventory/ReservationReleased.v1.schema.json), [ReservationExpired](schemas/inventory/ReservationExpired.v1.schema.json), each with a golden sample under [samples/](schemas/inventory/samples/).

| Property | Meaning |
|---|---|
| `status` | Open set: `Active` (created), `Committed`, `Released`, `Expired` |
| `lines` | 1 to 50 lines of distinct items, in ascending item ID order: `inventoryItemId`, `productId`, `variantId`, `quantity` (1 to 1,000,000) |
| `expiresAtUtc` | The expiry, in database time |
| `reference` | The owner's opaque reference (for example an order ID), or `null`; never personal data |
| `reservationVersion` | 1 when created, 2 after the terminal transition |

## Unknown open-set values (consumer obligations)

`movementType`, `adjustmentReason` and `status` are open sets (ADR-023 section 3). A consumer that meets an unknown value:

- keeps the raw value and processes the message
- fails closed: it never treats an unknown status as a known one, for example as `Committed`
- logs a Warning, and neither retries nor dead-letters the message (ADR-023 section 7)

## Evolution

- **Within version 1, only optional properties are added.**
- **A breaking change** (a removed, renamed or retyped property, or a changed meaning) publishes version 2 next to version 1 with the same `eventId` (dual publication), until the documented consumers have migrated.
- **Every published sample stays valid** against the current contract. The contract tests in `tests/Inventory/EnterpriseCommerce.Inventory.UnitTests/Infrastructure/IntegrationEventContractTests.cs` enforce this:
  - the schema snapshot
  - golden-sample round trips
  - producer conformance with the samples' shape

## Changelog

| Date | Change |
|---|---|
| 2026-11-24 | Version 1 of the five events, Phase 5 (ADR-028). |
