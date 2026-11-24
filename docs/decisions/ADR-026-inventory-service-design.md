# ADR-026: Inventory Service Design

- **Status:** Accepted
- **Date:** 2026-11-24
- **Phase:** 5 (Inventory)
- **Related:**
  - [ADR-003](ADR-003-database-per-service.md) and [ADR-011](ADR-011-per-service-data-store-assignment.md) (Inventory uses its own SQL Server database)
  - [ADR-006](ADR-006-redis-usage-boundaries.md) (Redis is never a system of record)
  - [ADR-009](ADR-009-selective-cqrs.md) and [ADR-010](ADR-010-no-mandatory-event-sourcing.md) (selective CQRS, no event sourcing)
  - [ADR-013](ADR-013-saga-based-workflow-coordination.md) (rule 7: reservation expiration is an explicit mechanism with an event)
  - [ADR-014](ADR-014-logical-tenant-isolation.md) (tenancy)
  - [ADR-024](ADR-024-catalog-service-design.md) (Catalog owns products, variants and SKUs)
  - [ADR-025](ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md) (merchant authorization)
  - [ADR-027](ADR-027-catalog-variant-verification-for-other-services.md) (Catalog variant verification)
  - [ADR-028](ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) (events and the outbox)
- **Resolves:** for Inventory, the PROJECT_DECISIONS section 13 row "Saga state machine, timeouts and reservation expiration design", **only as far as the reservation expiration mechanism goes** (section 7). The saga state machine, its timeouts and retry policies stay open for the checkout phase (ADR-013 rule 9).
- **Design and evidence:** [PHASE_5_INVENTORY.md](../architecture/PHASE_5_INVENTORY.md).

## Context

SERVICE_CATALOG gives Inventory these responsibilities:

- stock levels and stock adjustments
- inventory reservations and reservation expiration
- stock availability and inventory concurrency control

Its correctness "relies on SQL Server transactions, concurrency control and unique reservation rules; Redis locks are never the source of truth." Nothing below the service boundary was designed.

Facts that constrain the design:

- **Catalog identifies a sellable unit by its variant.** A variant has a stable GUID, a SKU unique per tenant (ignoring case) and an `IsActive` flag (ADR-024 section 3). **Catalog has no unit of measure.**
- **Catalog's variant set is mutable.** A full product `PUT` can remove a variant or change its SKU. Archive and restore change the product status. Catalog publishes no events (ADR-024 section 13).
- **Merchant roles live in Tenant & Merchant** and reach other services only as a per-request verdict (ADR-025).
- **Order does not exist.** It will orchestrate checkout and call Inventory to reserve, commit and release (ADR-013). No saga code may be written before its design (ADR-013 rule 9).
- **EF Core creates SQL Server databases with `READ_COMMITTED_SNAPSHOT` on** (Tenant & Merchant's migration tests assert it). A plain read is a snapshot read, so correctness may only rest on locking reads, conditional writes and constraints.

## Decision

### 1. Quantities: whole units, `int`

- **A quantity is a whole number of units**, stored as `int`. Catalog has no unit of measure, and every current product is sold per unit. Fractional quantities would need a unit-of-measure concept in Catalog first, and are a new decision (a decimal column with a fixed scale per unit), not a silent widening.
- **Bounds:**
  - On-hand stock is between 0 and 1,000,000,000 per item.
  - The initial stock of an item is between 0 and that maximum.
  - An adjustment changes on-hand stock by a non-zero amount of at most 1,000,000,000 in either direction, and never leaves the bounds.
  - A reservation line is between 1 and 1,000,000 units.

  These keep every sum inside `int` and refuse absurd input at the boundary.

### 2. Balance identity

- **One inventory item per (tenant, variant).** A unique index enforces it. A tenant cannot hold two independent balances for one variant.
- The item stores:
  - the tenant
  - Catalog's product and variant IDs
  - a **SKU snapshot** from the creation-time verification
  - on-hand and reserved quantities
  - a monotonically increasing **item version**
  - creation and modification times
  - a `rowversion`

  The SKU snapshot is informational, for listing and search: Catalog stays the owner of the SKU, and the snapshot can go stale after a Catalog change (section 11).
- **The item ID is Inventory's own** GUID (version 7). Cross-service references are IDs only, with no foreign keys to another service.

### 3. The availability invariant

- `Available = OnHand − Reserved` is **derived, never stored**.
- These rules hold at all times:
  - `OnHand ≥ 0`
  - `Reserved ≥ 0`
  - `Reserved ≤ OnHand`, so `Available ≥ 0`
  - `OnHand ≤ 1,000,000,000`
- The domain enforces them on every change. **Database CHECK constraints enforce them again**, so a path that bypasses the domain fails instead of corrupting a balance.

### 4. Stock adjustment semantics

- **A balance never changes except through a movement.** No API replaces a balance. A merchant states an explicit, signed change and a reason.
- **Reasons** are a closed set at the API, with sign rules:

  | Reason | Sign |
  |---|---|
  | `Receipt` (goods received) | positive |
  | `Return` (a customer return put back into stock) | positive |
  | `Damage` | negative |
  | `Loss` (shrinkage, theft) | negative |
  | `Correction` (an error fixed) | either |
  | `CycleCount` (a counted difference) | either |

  A stock count is recorded as the difference the merchant counted (`CycleCount`), never as an absolute value.
- **An adjustment cannot reduce on-hand stock below the reserved quantity.** Reserved stock is promised to a checkout. Such a request is refused with `409 insufficient-unreserved-stock`, and nothing is written.
- **Every adjustment is atomic.** The balance update, its movement record and its outbox row commit in one transaction or not at all.
- **Idempotency.** An adjustment requires an `Idempotency-Key` (section 8).

### 5. Movements: an append-only ledger

- **Every balance change is exactly one movement row per item:**
  - the initial stock
  - an adjustment
  - and per reservation line, each reservation transition (reserve, release, expire, commit)
- **A movement records:**
  - its type
  - the adjustment reason
  - the on-hand and reserved deltas
  - the on-hand and reserved values before and after
  - the item version it produced
  - the reservation, if any
  - the actor kind (`User`, `Service` or `System`) and actor reference (the Identity subject, the service client ID, or `reservation-expiry`)
  - the correlation ID and the time
- **Linearity.** The movement's item version is unique per item, and `after = before + delta` is a CHECK constraint. Replaying an item's movements in version order therefore reproduces its balance, and the tests verify this.
- **Immutability.** A trigger refuses every update and delete of a movement, the pattern of Tenant & Merchant's audit trail (ADR-021). Movements are never cleaned up in this phase; retention is a deployment decision.
- **This is not event sourcing (ADR-010).** The balance row is authoritative. The ledger is an audit trail written in the same transaction.

### 6. Reservation lifecycle

- **A reservation temporarily commits stock** to a caller, for future checkout orchestration. It has:
  - one tenant
  - 1 to 50 lines of distinct variants, with a quantity each
  - an owner (the creating service client)
  - an optional caller reference (1 to 64 characters of `A-Z a-z 0-9 - _ . :`, for example an order ID, never personal data)
  - an expiry time
- **States:** `Active`, then exactly one of `Committed`, `Released` or `Expired`. **Terminal states are never reopened.**

  | Request | On `Active` | On `Committed` | On `Released` | On `Expired` |
  |---|---|---|---|---|
  | commit | → `Committed`: on-hand and reserved both decrease by the line quantities | `200`, unchanged (idempotent) | `409 reservation-released` | `409 reservation-expired` |
  | release | → `Released`: reserved decreases, on-hand unchanged | `409 reservation-committed` | `200`, unchanged (idempotent) | `200`, unchanged (the stock is already free) |
  | expiry (worker) | → `Expired`: reserved decreases, on-hand unchanged | never | never | never |

- **Commit preserves the invariant.** On-hand and reserved fall by the same amount, so availability is unchanged. The units leave stock.
- **Release after expiry answers success with the actual state.** A compensating step that wants the stock freed has its outcome, and the response says `Expired`, so the caller knows the reservation was not released by its request.
- **Multi-line reservations are atomic.** Every line is checked against the locked balances first. If any line lacks available stock, the whole request fails with `409 insufficient-stock`, listing the short variants, and nothing is reserved.
- **Tenant boundaries.** Lines name variants of the route's tenant only. A variant without an inventory item in that tenant fails the whole request (`422 inventory-item-not-found`). A reservation can never consume another tenant's stock, because every lookup is scoped by the tenant.

### 7. Reservation expiration

- **Lifetime.** The default is **15 minutes**, which is long enough for a simulated payment step and short enough to free abandoned checkouts promptly.
  - `Inventory:Reservations:DefaultLifetime` sets the default, and `MaxLifetime` the maximum (both 1 minute to 24 hours, with the default at most the maximum).
  - A request may ask for `expiresInSeconds` between 60 and the maximum.
- **Time is the database's.** The expiry is computed from `SYSUTCDATETIME()` read inside the reservation's transaction, and every comparison uses the database clock too. Application clocks of several instances never disagree about an expiry.
- **The expiry worker.** A hosted service on every instance does the following:
  - It claims **one overdue reservation per short transaction** with `UPDLOCK, READPAST`, through a filtered index on active reservations.
  - It releases the reserved stock, writes the movements and the `ReservationExpired` and `StockLevelChanged` outbox rows, and marks the reservation `Expired`.
  - It processes at most `BatchSize` reservations (default 50) per cycle, every `Interval` (default 5 seconds, with jitter). It drains immediately while cycles are full.
- **Multi-instance safety.** `READPAST` makes instances skip each other's claimed rows, and the claim is the reservation's row lock. **No leader and no Redis lock is needed.** A reservation locked by a commit or release is skipped and re-examined on a later cycle.
- **Lazy expiry.** A commit or release of an `Active` reservation whose expiry has passed expires it **in its own transaction** first, and then answers as for `Expired`. The outcome therefore never depends on how far behind the worker is. A reservation is never committed after its expiry.
- **Exactly one transition.** Commit, release and expiry all lock the reservation row first and re-read its state under the lock. Two of them can never both transition it.
- **Recovery.** Nothing is held in memory. After a restart or crash, overdue reservations are found by the same query. A transaction interrupted mid-way rolls back completely.
- **Graceful stop.** The worker takes no new claim once the host stops. A claim in progress commits or rolls back within the lock timeout.
- **Known lag.** Between its expiry time and its expiration, an overdue reservation still counts as reserved for *new* reservations. Availability is then under-reported, never over-reported, by at most the worker lag. The `expiry` health check reports overdue reservations older than a threshold.

### 8. Idempotency

- **Merchant item creation and adjustments** require `Idempotency-Key` (1 to 100 characters of `A-Z a-z 0-9 . _ : -`, the repository convention).
  - **Scope:** (tenant, user subject, key), as Catalog's product creation.
  - The key and a SHA-256 hash of the canonical request are stored on the created row: the item for a creation, the movement for an adjustment. A filtered unique index on the scope backs this.
- **Reservations** require `Idempotency-Key`.
  - **Scope:** (service client, tenant, key). One client never receives another client's reservation, and the same key in two tenants creates two reservations.
  - The key and request hash are stored on the reservation row behind a unique index. A transaction-scoped application lock on the scope serializes concurrent retries of one key **before** any stock is locked, so a retry that arrives during the original waits and then replays it, instead of reserving again or failing for insufficient stock.
- **Answers** (the repository convention):
  - the first request: `201`
  - the same key and an equivalent request: `200` with the same resource in its **current** state. That is the original logical result; a replayed reservation that has since been committed says so.
  - the same key and a different request: `422 idempotency-key-reused`, with nothing written
- **Failed attempts store nothing.** For example, an attempt that lacked stock. A retry with the same key is evaluated again. That cannot duplicate an effect, because none happened.
- **Commit and release need no key.** They are idempotent state transitions of the reservation, which is their identity.

### 9. Concurrency and transactions

- **Every write is one short `READ COMMITTED` transaction** with `SET LOCK_TIMEOUT 5000`.
  - A deadlock victim retries the whole transaction up to 3 times with jittered backoff.
  - A lock timeout or an unavailable database answers `503` with `Retry-After`.
  - This is Tenant & Merchant's write-gate pattern.
- **Locking reads before any change.** Rows are read with `UPDLOCK, ROWLOCK`, which holds an update lock to the end of the transaction and reads the latest committed version even under `READ_COMMITTED_SNAPSHOT`. Two requests can therefore never both observe the same available stock. **No read-then-write sequence runs without a lock.**
- **A fixed lock order** prevents deadlocks between the paths:
  1. the reservation idempotency application lock (creation only)
  2. the reservation row (commit, release, expiry)
  3. the inventory items, **one at a time in ascending ID order**
- **Backstops.** A `rowversion` concurrency token on every mutable row (a conflict answers `409 concurrency-conflict`), then the CHECK constraints and unique indexes.
- **Outbox rows are written after the locks are taken**, in the same transaction. Two transactions that change one aggregate therefore produce outbox rows in commit order (ADR-028).

### 10. Merchant permissions

Merchant requests are authorized per request through Tenant & Merchant's membership verification, exactly as in ADR-025 section 2:

- the subject comes from the validated `ec-merchant` user token
- the tenant comes from the route
- the verdict is never cached
- the service fails closed with `503`
- no active membership answers `404 tenant-not-found`, and an insufficient role `403 permission-denied`

Inventory's own permissions:

| Permission | `MerchantOwner` | `MerchantAdmin` | `MerchantStaff` |
|---|---|---|---|
| `ViewInventory` (items, balances, availability, movements) | yes | yes | yes |
| `ManageInventory` (create items, adjust stock) | yes | yes | no |

- **Staff is read-only.** Changing stock changes what the marketplace can sell and is an administrative act. An unknown role grants nothing and is logged.
- **Merchants have no reservation endpoints.** Reservations belong to checkout orchestration (section 6). Merchants see their effect through balances and movements.
- **Internal reservation endpoints** are for allow-listed service clients only, with audience `inventory-api` and scope `svc.inventory.reservations`. The allow-list is empty by default. **A reservation is visible only to the client that created it**; another allowed client gets `404`.

### 11. Catalog facts, archival and stale references

- **Verified once, at item creation**, through Catalog's internal variant verification (ADR-027):
  - the product exists in the tenant
  - the variant belongs to that product
  - the product is not `Archived`

  `Draft` and `Published` products are both eligible, so merchants can stock a product before publishing it. Inactive variants are eligible too: `IsActive` controls sellability, which is not Inventory's concern. The SKU is recorded as a snapshot.
- **Never verified again.** Adjustments, reads and reservations never call Catalog. Stock operations and checkout must not depend on Catalog's availability, and Catalog has no events to keep a projection current.
- **Archival keeps everything.** When a product is archived, or a variant removed or re-SKU'd, in Catalog after its item exists:
  - **Inventory never deletes the item, its movements or its reservations.** Stock and history stay auditable.
  - Existing reservations finish normally: commit, release or expiry.
  - New reservations are not refused by Inventory. **Sellability is checked by Order at checkout** (PROJECT_DECISIONS section 8, step 2: Order validates the cart and the product information) before it reserves.
  - The SKU snapshot may be stale.
- **A documented limitation,** removed once Catalog publishes product events and Inventory projects them (that needs ADR-024 section 13's replica set and outbox design).

### 12. Selective CQRS and structure

- Commands go through domain aggregates (`InventoryItem`, `Reservation`) inside the write gate.
- Queries (lists, movement history) read projections directly with `AsNoTracking`, with offset paging: page from 1, page size 20 by default and at most 100, and `totalCount`.
- No mediator and no generic repository (ADR-009).

## Consequences

- **Positive:**
  - Overselling is prevented by database locks and constraints, not by application timing.
  - Every balance is explained by an immutable ledger.
  - Reservations cannot be duplicated by retries, and cannot transition twice.
- **Positive:**
  - Expiry is exact for commit and release, because it is lazy, and bounded for availability, through the worker.
  - Expiry needs no leader and no Redis.
- **Positive:** stock operations do not depend on Catalog's availability.
- **Negative: same-item contention.** Reservations of one popular item serialize on its row lock, and the throughput per item is bounded by one short transaction at a time. Independent items do not contend. Measurements are in PHASE_5_INVENTORY.md; no target is claimed.
- **Negative:** Inventory does not learn of Catalog archival or SKU changes. Order must check sellability itself.
- **Negative:** availability is under-reported for overdue reservations until the worker runs.
- **Negative:** the movement ledger grows without bound until a retention decision is made.

## Alternatives considered

- **Decimal quantities now:** no unit of measure exists, so precision rules would be arbitrary. Rejected until Catalog models units.
- **Product-level stock:** a product's variants are distinct sellable units with their own SKUs (ADR-024). Rejected.
- **Allowing a balance to be set directly:** it hides the reason and the delta, and races with reservations. Rejected in favour of explicit deltas.
- **Optimistic concurrency only (a `rowversion` retry loop) for reservations:** under contention on one item, most attempts fail and retry, so latency becomes unpredictable. Locking reads give a deterministic result per request. Rejected as the primary mechanism and kept as a backstop.
- **One conditional `UPDATE ... WHERE OnHand - Reserved >= @q` per line:** correct for a single line. But the domain rules and the movement's before and after values would move into SQL, and a multi-line request still needs a transaction with ordered locks. Rejected in favour of locked reads and domain changes.
- **A per-tenant lock for all writes:** it would make the outbox order per tenant trivially commit order, but it serializes every item of a merchant. Rejected (ADR-028 defines the ordering guarantee per aggregate).
- **A single leader for expiry (an application lock, like the outbox relay):** expiry needs no ordering, and `READPAST` claims are safe across instances. Rejected as unnecessary.
- **Redis locks or TTL keys for reservations:** forbidden as a correctness mechanism (ADR-006, SERVICE_CATALOG). Rejected.
- **Expiry from the application clock:** instances can disagree. Rejected in favour of the database clock.
- **Storing failed reservation outcomes for replay:** a retry after a restock would keep failing with no benefit, because a failure has no effect to protect. Rejected.
- **Verifying Catalog on every reservation:** it couples checkout's availability to Catalog's, and Order checks sellability anyway. Rejected.
- **Reservation endpoints for merchants:** no merchant use case exists. Rejected for now.
