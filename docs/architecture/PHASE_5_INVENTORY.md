# Phase 5: Inventory

- **Status:** complete (2026-11-24). Tested locally against SQL Server 2022 and Kafka 4.4.0. Not CI-verified until pushed, and not production-verified (section 12).
- **Decisions:**
  - [ADR-026](../decisions/ADR-026-inventory-service-design.md) (Inventory design)
  - [ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md) (Catalog variant verification)
  - [ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) (events and the shared outbox block)
- **Contracts:** [INVENTORY_API_CONTRACT.md](../api/INVENTORY_API_CONTRACT.md), [INVENTORY_EVENTS.md](../events/INVENTORY_EVENTS.md), and the Catalog addition in [CATALOG_API_CONTRACT.md section 6a](../api/CATALOG_API_CONTRACT.md#6a-internal-variant-verification).
- **Operations:** [INVENTORY_OPERATIONS.md](../operations/INVENTORY_OPERATIONS.md).

## 1. Inputs and scope

**Inputs:**

- the owner's Phase 5 authorization of 2026-11-24
- SERVICE_CATALOG section 4
- PROJECT_DECISIONS sections 5, 8 and 9
- ADR-013 rule 7
- ADR-014, ADR-017, ADR-023 to ADR-025
- the Catalog and Tenant & Merchant implementations, which were read, not assumed

**In scope:**

- the Inventory service: four layers under `src/Services/Inventory`, with its own SQL Server database
- merchant stock management
- the reservation lifecycle through internal endpoints
- reservation expiry
- integration events through the transactional outbox
- Catalog's internal variant verification
- the Gateway route, the Compose topic and the development keys
- the extraction of `EnterpriseCommerce.BuildingBlocks.Messaging` (ADR-023 section 8)

**Out of scope, by the authorization and the ADRs:**

- Cart, Order, Payment and Fulfillment, and any placeholder Order
- saga code (ADR-013 rule 9)
- consumers and inboxes (no Inventory use case consumes)
- Catalog events (no MongoDB outbox)
- Redis
- application containers

## 2. Architecture

```
merchant portal (ec-merchant user token)        future Order (svc- token, inventory-api)
          │  /api/v1/inventory/** via Gateway            │  /internal/v1/inventory/** (never via Gateway)
          ▼                                              ▼
 ┌──────────────────────── Inventory.Api ────────────────────────┐
 │ JWT (BuildingBlocks.Authentication) · merchant-user / reservation-service policies │
 └──────────┬───────────────────────────────────────────┬────────┘
            ▼                                           ▼
   Application: InventoryAccessAuthorizer ──► IMembershipVerifier ──► Tenant & Merchant (svc-inventory)
                CreateInventoryItemHandler ─► ICatalogVariantVerifier ─► Catalog (svc-inventory)
                AdjustStockHandler · CreateReservationHandler · ReservationHandlers
            │ IInventoryWriteGate / IInventoryQueries
            ▼
   Infrastructure: InventoryWriteGate (locks, database clock, deadlock retry) · InventoryDbContext
                   (movements + outbox at save) · ReservationExpiryWorker · relay (BuildingBlocks.Messaging)
            ▼                                                     ▼
   SQL Server: InventoryItems · StockMovements · Reservations · ReservationLines · OutboxMessages ──► Kafka inventory.events
```

- **Layers** follow ADR-002, and the architecture tests enforce them:
  - Domain references nothing.
  - Application references the Domain only.
  - Infrastructure references EF Core, SQL Server and the messaging block.
  - Api references ASP.NET Core and the authentication block.
- **No mediator and no generic repository.** The handlers are plain classes. The write gate exposes only the locking reads the use cases need.
- **Selective CQRS:** commands go through the aggregates inside the gate, and queries read untracked projections (`IInventoryQueries`).

## 3. Domain and data ownership

- **Inventory owns:**
  - stock balances (`InventoryItems`)
  - the movement ledger (`StockMovements`)
  - reservations and their lines
  - the outbox rows
- **Inventory holds only references** to others' data:
  - Catalog's product and variant IDs and a SKU snapshot, verified at item creation
  - Identity subjects and service client IDs, in audit columns only
  - Tenant IDs
- **No foreign key or query reaches another service's database.**
- **Aggregates:**
  - `InventoryItem` is the balance with `Version`, and records one `StockMovement` and one `StockLevelChanged` per change.
  - `Reservation` holds immutable lines, transitions exactly once, and changes its items' balances through their internal methods.
- **Invariants** (ADR-026 section 3) are domain rules *and* CHECK constraints: `0 ≤ Reserved ≤ OnHand ≤ 1,000,000,000`, with `Available` derived.
- **Movements** are linear: `after = before + delta`, and (item, version) is unique. They are append-only (`TR_StockMovements_AppendOnly`). The save refuses an item changed without a movement.
- **Quantities** are whole units (`int`). Fractional input answers `400` (ADR-026 section 1).

## 4. Database schema and migrations

| Table | Key and constraints | Indexes |
|---|---|---|
| `InventoryItems` | PK `Id`. Checks: on-hand, reserved ≤ on-hand, version. `rowversion`. | `UX (TenantId, VariantId)`, `UX (TenantId, CreatedBySubject, CreationKey)`, `IX (TenantId, CreatedAt, Id)`, `IX (TenantId, ProductId)`, `IX (TenantId, Sku)` |
| `StockMovements` | PK `Id`. FKs to item and reservation. Checks: type, reason, actor kind, balance linearity, shape per type. Append-only trigger. | `UX (InventoryItemId, ItemVersion) INCLUDE TenantId`, `UX (TenantId, ActorId, IdempotencyKey) WHERE key NOT NULL`, `IX ReservationId` (filtered) |
| `Reservations` | PK `Id`. Checks: status, completion, expiry, version. `rowversion`. | `UX (OwnerClientId, TenantId, IdempotencyKey)`, `IX ExpiresAt WHERE Status = 'Active'` |
| `ReservationLines` | PK (`ReservationId`, `InventoryItemId`). Quantity check. Immutable trigger. | `IX InventoryItemId` |
| `OutboxMessages` | The shared mapping (`OutboxModel`) | Pending and published filtered indexes, `UX (EventId, EventVersion)` |

**Migrations:**

- **`InitialInventorySchema`:** the schema above. Its `Down` refuses while any stock, history or unpublished event exists (error 50100).
- **`InventoryListingOrder`:** the listing index on creation time (finding F1, section 10). Non-destructive and reversible.

The database is created with `READ_COMMITTED_SNAPSHOT` on, by EF Core. Every decision rests on locking reads, which hold either way. Procedures: [INVENTORY_OPERATIONS.md section 5](../operations/INVENTORY_OPERATIONS.md#5-migrations).

## 5. Security model and tenant isolation

- **Token validation** is local (ADR-017 section 4). It accepts the audience `inventory-api` for services and `ec-merchant` for users. A token of another audience, issuer or key, an expired token, or a token that is not `at+jwt` answers `401`.
- **Merchant requests:**
  - The subject comes from the token, and the tenant from the route.
  - Tenant & Merchant's verdict is checked on every request and never cached.
  - No membership answers `404 tenant-not-found`, and a role without the permission `403`.
  - Forged tenant and role claims are ignored.
  - Staff is read-only (ADR-026 section 10).
  - Without a verdict the request answers `503`, and nothing is read or written.
- **Service requests:**
  - the reservation policy: audience, scope `svc.inventory.reservations`, an `svc-` client with `sub == client_id`, and the allow-list, which is empty by default
  - a reservation is visible only to its creating client
  - a user token answers `403`
- **Isolation:**
  - every query and lock is scoped by the route's tenant
  - a reservation line can only reference the tenant's own items
  - another tenant's IDs answer `404` or `422`
- **Inventory's own calls** use `svc-inventory`, with `private_key_jwt`, a token per scope, and the Phase 4 signing-key fix.
- **No secrets are committed.** Keys come from `deploy/dev/bff-dev-keys.cs`, outside the repository, and allow-lists are empty in committed configuration.
- **No logs or metrics carry high-cardinality or sensitive values.** Events carry no client, subject, actor or key.

## 6. Reservation and concurrency design

- **Transactions.**
  - Every write is one `READ COMMITTED` transaction with `SET LOCK_TIMEOUT 5000`.
  - The database clock (`SYSUTCDATETIME()`) is read first.
  - A deadlock victim, including an application-lock deadlock, retries the whole transaction up to 3 times.
- **Lock order:**
  1. the reservation idempotency application lock (`sp_getapplock`, transaction-owned, on a hash of client, tenant and key)
  2. the reservation row
  3. the items, one locking read each in ascending ID order

  Overlapping multi-line requests in opposite line order were tested: they do not deadlock or oversell.
- **No read-then-write without a lock.** `UPDLOCK, ROWLOCK` reads see the latest committed row, also under RCSI. `rowversion` and the CHECK constraints are the backstops.
- **Placement** checks every line before any change, so it is all or nothing.
- **Idempotency.**
  - The application lock makes a concurrent retry wait and then replay the original.
  - The unique index on the scope is the backstop.
  - A replay answers the reservation in its current state.
- **Transitions.**
  - Commit, release and expiry lock the reservation and re-read it, so a reservation transitions exactly once.
  - An overdue active reservation expires in the request.
- **Expiry worker.**
  - It claims `TOP (1)` overdue reservations with `UPDLOCK, READPAST`, one per short transaction, in batches of 50 every 5 seconds with jitter, on every instance.
  - It needs no leader.
  - Interrupted claims roll back and are retried later.
- **Outbox order.** Locks are taken before the outbox insert, so one aggregate's rows are in commit order. Per-aggregate order is the guarantee (ADR-028 section 3).

## 7. Integration events and the relay

- **The events:** `StockLevelChanged` (one per movement, `eventId = movementId`), `ReservationCreated`, `ReservationCommitted`, `ReservationReleased` and `ReservationExpired`. All are v1, keyed by tenant, on `inventory.events`.
- **Contracts and tests.** The schemas and golden samples are committed. The contract tests check the schema snapshot, the round trips, producer conformance, the excluded data and the 50-line size.
- **The relay** is `BuildingBlocks.Messaging` with Inventory's profile:
  - one leader
  - sequential acknowledged publishing (`acks=all`, idempotent producer)
  - durable attempts and backoff
  - a halt, never a skip
  - cleanup after 7 days
  - `/health/outbox` outside readiness
  - mutual TLS required outside Development
- **The extraction** (ADR-028 section 4) moved the envelope, serializer, model mapping, relay, leadership, store, publisher, options, health and meters out of Tenant & Merchant. Tenant & Merchant's behaviour is unchanged, and its 447 unit and 219 SQL Server and Kafka tests pass on the block.

## 8. Milestones

| # | Milestone | Commit |
|---|---|---|
| 1 | Design decisions (ADR-026 to ADR-028) | `0d6e2a7` |
| 2 | `BuildingBlocks.Messaging`; Tenant & Merchant moves to it | `7e07c12` |
| 3 | Service scaffold and domain | `c3e1ad2` |
| 4 | SQL Server persistence, migration, outbox mapping and event contracts | `aebef6d` |
| 5 | Catalog variant verification endpoint | `741b7b2` |
| 6 | Merchant stock management with membership and Catalog verification | `765856a` |
| 7 | Reservation lifecycle (internal API) | `15d21d7` |
| 8 | Reservation expiry worker | `e5ab56f` |
| 9 | Kafka relay, Gateway route, topic and development keys | `cabe52b` |
| 10 | Measurements, regression fixes, evidence and closure | the closing commit |

## 9. Test strategy and evidence

**Strategy.** Correctness is proved against the real infrastructure: SQL Server 2022 (`mcr.microsoft.com/mssql/server:2022-CU28-ubuntu-22.04`, Compose) and Kafka 4.4.0 (KRaft, Compose). There is no in-memory provider. Concurrency tests release N callers at a `Barrier` and repeat in rounds.

| Area | Where | What it proves |
|---|---|---|
| Domain | `Inventory.UnitTests/Domain` | invariants, sign rules, placement atomicity, every transition, lazy expiry, ledger replay |
| Contracts | `Inventory.UnitTests/Infrastructure` | the mapper, the schemas and samples, the excluded data |
| Settings and authorization | `Inventory.UnitTests/Application` | the role matrix, Catalog eligibility, the startup rules in and outside Development |
| Persistence | `Inventory.IntegrationTests/Persistence` | the migration from empty; rollback refused or allowed; every constraint, index and trigger; RCSI; atomicity of balance, movement and event; fail closed; concurrent adjustments and creations |
| Merchant API and security | `.../Api/MerchantInventoryApiTests`, `MerchantSecurityTests`, `HostConventionsTests` | creation with Catalog's verdict; adjustments; idempotency (including concurrent retries); listing and paging; roles; cross-tenant isolation; forged claims; token failures; Tenant & Merchant and Catalog outages (`503`, nothing written); no verdict caching; correlation propagation; no tokens in logs; health |
| Reservations | `.../Api/ReservationApiTests` | **one unit and 16 concurrent reservations, 25 rounds: exactly one success, reserved 1, available 0**; 20 callers for 7 units; opposite-order multi-line requests; a short line reserves nothing; idempotency scopes and concurrent retries; commit and release once; concurrent commit and release (15 rounds); lazy expiry; owner isolation; the policy; validation; adjustments and reservations together with ledger sums |
| Expiry | `.../Reservations/ReservationExpiryTests` | effects; two workers over 200 overdue reservations (each expired once); expiry racing commit and release (15 rounds); an interrupted claim skipped and recovered; the hosted worker and `/health/expiry` |
| Kafka | `.../Kafka/KafkaPublicationTests` | end to end with envelope, headers, key and per-aggregate order; refused requests publish nothing; backlog recovery by a new host; redelivery with the same ID and bytes; a missing topic halts without skipping and resumes |
| Catalog | `Catalog.IntegrationTests/Api/VariantVerificationApiTests` | the verdicts, tenant isolation, every refused caller, malformed bodies |
| Gateway | `DevelopmentRouteTests`, `InternalPathExposureTests` | the route shape and forwarding; Inventory and verification internal paths blocked in traversal, encoded and case forms |
| Architecture | `ArchitectureTests` | Inventory's layers; the messaging block's boundaries |

**Results** (2026-11-24, Release, `EC_REQUIRE_INFRA_TESTS=true`, Compose SQL Server 2022, Kafka 4.4.0, MongoDB 8.0, session Redis; browser tests with `EC_TEST_BROWSERS=true`). Each module was run separately for the final run:

| Module | Baseline (before Phase 5) | Final |
|---|---|---|
| ArchitectureTests | 169 | 222 |
| BuildingBlocks.Authentication.UnitTests | 11 | 11 |
| BuildingBlocks.Messaging.UnitTests | – | 11 |
| Identity.UnitTests | 169 | 169 |
| Identity.IntegrationTests | 166 | 166 |
| TenantMerchant.UnitTests | 447 | 447 |
| TenantMerchant.IntegrationTests | 219 | 219 |
| Catalog.UnitTests | 121 | 121 |
| Catalog.IntegrationTests | 122 | 136 |
| Inventory.UnitTests | – | 90 |
| Inventory.IntegrationTests | – | 127 (122 run; 5 opt-in measurements skipped) |
| Gateway.IntegrationTests | 85 | 101 |
| Bff.UnitTests | 103 | 103 |
| Bff.IntegrationTests | 63 | 63 |
| Bff.BrowserTests | 18 | 18 |
| **Total** | **1,693** | **2,004** |

**The full-solution parallel run** (all modules at once, browser tests skipped) gave 2,004 tests: 1,979 passed, 2 failed and 23 skipped. The two failures were F3 and F4 below; both were fixed, and their suites then passed.

**The final per-module run** passed every module except one test of `Catalog.IntegrationTests` (135 of 136). Its name was not captured, and 4 further runs of that suite were green (136 of 136). The only known intermittent Catalog test is TS-4 (F5), but this occurrence is **not demonstrated** to be it.

## 10. Findings during the phase

| ID | Finding | Resolution |
|---|---|---|
| F1 | **SQL Server orders `uniqueidentifier` values by their last bytes first**, so ordering by a version 7 ID is not creation order. Found by the listing test. | The listing orders by `CreatedAt, Id`, and migration `InventoryListingOrder` replaces the index. Event and response lines are sorted explicitly, and the lock order uses .NET's GUID order everywhere. |
| F2 | **The startup validation hid the endpoint errors:** the endpoint options validated against an already failing `IOptions<ServiceClientOptions>`. | They read the raw section instead, so every problem is reported. |
| F3 | **Under full-solution load, one of 16 queued writers of one item waited past `LOCK_TIMEOUT` (5 s)** and answered `503`. This is the designed fail-closed behaviour; the test had assumed every writer succeeds. | The test asserts the invariant instead: every applied change is recorded exactly once and a refused one leaves nothing. |
| F4 | **TS-3 recurred** for `SelfServiceOnboardingTests.AnOutOfRangeLimit_StopsTheHost` (Phase 3) under full load: `ObjectDisposedException` from a failing `WebApplicationFactory` host. | As TS-3 prescribes, the test uses the `IStartupValidator` without a web host, with the same assertion. |
| F5 | **TS-4:** Catalog's chunked oversized body to `/brands` sometimes answered `400` instead of `413` (a Phase 4 test). | **Resolved** in the reliability review: the test's body was both oversized and malformed, so two correct refusals raced. The root cause, the evidence and the fix are in [docs/testing TS-4](../testing/README.md). |
| F6 | **A Kafka test failure, first unexplained:** `AMissingTopic_...` failed once (14 s). The runner script had filtered its output away. | **Resolved** in the reliability review as **TS-5**. A timed copy of the test, recorded with TRX, reproduced it: `KafkaException: Local: Timed out` from a 10-second metadata call. The Compose broker advertised `localhost`, which resolves to `::1` first on Windows, while its port was published on `127.0.0.1` only, and each refused IPv6 connect took about 2 s. The broker now advertises `127.0.0.1:9094`. No timeout or retry changed. Evidence in [docs/testing TS-5](../testing/README.md). |

## 11. Measurements

**Environment:**

- Machine: Windows 11, Intel Core i5-10300H (4 cores, 8 threads), 15.9 GB RAM
- Docker Desktop (WSL2): 8 CPUs, about 8.3 GB
- SQL Server 2022 CU28 container (2.5 GB limit) and Kafka 4.4.0 (1 GB limit), on the same host
- The service runs in-process on the ASP.NET Core test server (`WebApplicationFactory`), so HTTP network cost is not measured
- Release build; `InventoryMeasurements`, `EC_TEST_MEASURE=true`
- One run per scenario. Latencies are per request, with the operations and concurrency stated

| Scenario | Concurrency | Operations | Elapsed | Throughput | p50 ms | p95 ms | p99 ms |
|---|---|---|---|---|---|---|---|
| Single-unit reservations, **one item** (contended) | 32 | 800 | 7.54 s | 106/s | 265 | 539 | 603 |
| Single-unit reservations, **one item per caller** (independent) | 32 | 800 | 2.66 s | 301/s | 89 | 190 | 442 |
| 10-line reservations over 20 shared items (first run; see the review below) | 16 | 320 | 15.51 s | 21/s | 143 | 744 | 4,197 |
| Item list, first page (20), 10,001 items | 1 | 20 | – | – | 5.0 | 7.5 | 8.3 |
| Item list, page 100 of 20 | 1 | 20 | – | – | 8.5 | 9.6 | 9.6 |
| Item list, last page of 100 | 1 | 20 | – | – | 21.8 | 26.0 | 26.1 |
| Item list, SKU filter | 1 | 20 | – | – | 3.1 | 3.9 | 5.0 |
| Movements, newest page (20), 100,001 movements on one item | 1 | 20 | – | – | 22.6 | 24.7 | 24.9 |
| Movements, page 5,000 of 20 | 1 | 20 | – | – | 251.7 | 298.3 | 303.8 |
| Outbox drain, one leader, sequential acknowledged publishing | 1 | 5,000 messages | 7.37 s | 678/s | – | – | – |

**Observations.** They are not targets and not production expectations.

- **Same-item contention serializes on the item's row lock,** as designed. Independent items reached about 3 times the throughput.
- **Overlapping 10-line reservations** take 10 locking round trips each and queue behind each other. Their p99 reaches the 5-second lock timeout, beyond which a request answers `503` (see the review below).
  - This is the main bottleneck observed.
  - Options, if real load demands them, each with its own evidence: a single ordered locking statement per request, a higher lock timeout, or fewer lines per reservation.
- **Offset paging grows with depth.** The movement history's deep pages, and the `COUNT` of a 100,000-movement item, dominate. Keyset paging (by `ItemVersion`) is the remedy if deep history pages become a use case.
- **The relay publishes sequentially.** Its rate bounds the event throughput (ADR-023 Consequences). Each reservation of *n* lines produces *n* + 1 events.

### The 10-line scenario in the reliability review

The reliability review re-ran the 10-line scenario to check that it is reproducible.

**The first measurement could not be repeated as written:** it asserted that every request succeeds, and in 5 of 9 runs one request answered `503` and stopped it. The measurement now counts those refusals instead. It asserts that each one is a lock timeout: the exception handler's `DataStoreUnavailableException` whose cause is SQL Server error 1222. It also asserts that the accepted reservations alone hold the reserved stock. It also reports SQL Server's lock waits (`sys.dm_os_wait_stats`, `LCK_M_*`, server-wide, so it runs alone) and the gate's deadlock retries (log event 5020). A disjoint baseline gives every caller 10 items of its own, so it has the same 10 locking round trips without shared rows. Transactions, lock order and the 5-second lock timeout are unchanged.

Six runs, each scenario once per run, same environment as above:

| Scenario (16 callers, 320 requests) | Throughput | p50 ms | p95 ms | p99 ms | Refused at the lock timeout | Lock waits per run | Deadlock retries |
|---|---|---|---|---|---|---|---|
| Over 20 shared items (contended) | 19.5 to 27.7/s | 141 to 154 | 790 to 1,082 | 4,476 to 5,181 | 7 of 1,920 requests (0.36 %), in 4 of 6 runs | 522 to 524, 78 to 86 s in total | 0 |
| Disjoint items (baseline) | 42 to 123/s (typically about 110/s) | 110 to 137 | 202 to 258 | 276 to 322 | 0 | 0 to 6, at most 7 ms | 0 |

**The dominant latency source is lock queueing on the shared rows, not the round trips:**

- The median request costs about the same in both scenarios (about 145 ms against about 120 ms), so the 10 locking statements and the inserts set the typical latency.
- The tail differs by more than ten times (p99 about 4.6 s against about 0.3 s). The contended run waits about 80 s for locks in total, about a third of all request time. Each request holds up to 10 row locks for its whole transaction, and each caller's 10 items overlap 9 other callers' sets, so waits chain into convoys.
- No deadlock occurred: the ascending lock order holds.

**The result is reproducible, with one correction:**

- About 21/s, and a p99 of 4.2 to 5.2 s across the 11 contended runs of the review.
- The p99 does not stay below the 5-second lock timeout. A few requests in a thousand exceed it and are refused with `503`, the designed fail-closed behaviour (ADR-026 section 9). Nothing is reserved for them, and the caller may retry.
- Whether that is acceptable depends on real reservation shapes. The options above remain, each needing its own evidence. None was applied for a local benchmark.

## 12. Known limitations and production gates

**Limitations:**

- **Catalog changes after item creation are not seen,** because Catalog has no events: archival, variant removal and SKU changes (ADR-026 section 11). Order must check sellability at checkout.
- **Availability is under-reported for overdue reservations** until the worker runs. It is never over-reported.
- **Cross-aggregate event order within a tenant** is not guaranteed (ADR-028 section 3).
- **Same-item throughput is bounded** by one short transaction at a time. Multi-line contention is the main bottleneck: under the measured overlap, a few requests in a thousand reach the 5-second lock timeout and are refused with `503` (section 11).
- **The movement ledger has no retention** yet.
- **The `svc-` token provider is now copied in three services** (Tenant & Merchant, Catalog, Inventory): a building-block candidate (deferred).
- **No consumer and no Order:** the reservation API is exercised by test clients only.

**Production gates (open):**

- the `svc-inventory` key in a managed key store
- the deployed Identity client and the allow-list entries at Tenant & Merchant and Catalog
- a deployed reservation allow-list, once Order exists
- Kafka with mutual TLS, and the production topic settings (replication 3, `min.insync.replicas=2`, partitions from measured load)
- deployed Gateway routes and TLS
- a metrics exporter and the alerts of INVENTORY_OPERATIONS.md section 4
- the ledger retention decision
- load measurement on production-like hardware
- **a green CI run** (nothing is pushed)

**Deferred work:**

- the shared token-provider building block
- keyset paging for movements
- a merchant view of reservations
- a Catalog projection once Catalog publishes events
- the saga design (the checkout phase)

**Deviations from the Phase 5 plan:**

- **Tenant & Merchant's relay tests stay in its own test projects,** with their assertions unchanged, instead of moving to the block's test project. The block got 11 tests of its own for what it adds.
- **The token provider was copied, not extracted.** A third copy is now a building-block candidate (deferred above).

## 13. Completion report

- **Delivered:**
  - an independent four-layer Inventory service on its own SQL Server database
  - merchant stock management behind Tenant & Merchant's authorization
  - Catalog-verified items
  - an immutable ledger
  - atomic, idempotent reservations that do not oversell under the tested concurrency
  - commit, release and expiry that transition exactly once
  - transactional events published by the shared relay
  - the Gateway route, the Compose topic, the development keys
  - health, metrics and logs
  - the documentation and ADRs listed above
- **Changed outside Inventory:**
  - **Tenant & Merchant:** now on `BuildingBlocks.Messaging`, with unchanged behaviour. One Phase 3 test moved to its TS-3 approach (F4).
  - **Catalog:** the variant verification endpoint and its allow-list.
  - **Gateway:** one route and one cluster.
  - **Development:** the key script, the topic manifest, the architecture tests and the documentation.
- **Decisions affected:**
  - ADR-023 is amended for Inventory's ordering, and its section 8 is applied.
  - PROJECT_DECISIONS section 13: the reservation expiration mechanism is resolved; the saga state machine and its timeouts stay open.
- **Final regression:** section 9.
