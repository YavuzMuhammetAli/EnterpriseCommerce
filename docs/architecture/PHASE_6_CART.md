# Phase 6: Cart

- **Status:** implemented and tested locally (2026-11-24), against SQL Server 2022 and Redis 8.8. **Not CI-verified until pushed**, and not production-verified (section 12).
- **Decisions:**
  - [ADR-029](../decisions/ADR-029-cart-service-design.md) (Cart design)
  - [ADR-030](../decisions/ADR-030-cart-redis-cache-consistency.md) (the Redis cache and its consistency)
- **Contract:** [CART_API_CONTRACT.md](../api/CART_API_CONTRACT.md). It depends on Catalog's public product read, [CATALOG_API_CONTRACT.md section 2.2](../api/CATALOG_API_CONTRACT.md#22-read-a-published-product).
- **Operations:** [CART_OPERATIONS.md](../operations/CART_OPERATIONS.md).

## 1. Inputs and scope

**Inputs:**

- the owner's Phase 6 authorization of 2026-11-24
- SERVICE_CATALOG section 5
- PROJECT_DECISIONS sections 5, 6, 8 and 11
- ADR-006, ADR-011, ADR-014, ADR-016, ADR-017, ADR-024 and ADR-026
- the Identity, BFF, Catalog, Inventory and Gateway implementations, which were read rather than assumed

**In scope:**

- the Cart service: four layers under `src/Services/Cart`, with its own SQL Server database
- a Redis cache on the existing cache Redis
- the customer cart API
- the Catalog product snapshot through the public read
- cart expiry and its cleanup
- the Gateway route, CI support and local development settings

**Out of scope, by the authorization and the ADRs:**

- stock reservation, or any call to Inventory
- checkout, orders, payments and fulfillment
- saga code (ADR-013 rule 9)
- guest carts and multi-merchant carts
- integration events (no consumer needs one)
- application containers, as in every earlier phase

## 2. Architecture

```
storefront browser ──► storefront BFF (/api/**, session's ec-storefront token) ──► Gateway (/api/v1/cart/**, localhost:7000)
                                                                                          │
 ┌──────────────────────────────────── Cart.Api ──────────────────────────────────────────▼──┐
 │ JWT (BuildingBlocks.Authentication, audience ec-storefront) · customer policy · fallback   │
 │ CartEndpoints: subject from the token only · ETag / If-Match · no-store · problem details  │
 └──────────────┬──────────────────────────────────────────────────────────┬─────────────────┘
                ▼                                                          ▼
   Application: CartReader (cache ► SQL)                     CartCommands ──► ICatalogProductReader ──► Catalog
                                                                  │            (GET /api/v1/catalog/products/{id}, anonymous)
                │ ICartQueries · ICartCache                       │ ICartStore (optimistic, 3 attempts)
                ▼                                                 ▼
   Infrastructure: RedisCartCache (Lua CAS on rowversion, circuit, repair) · CartStore · CartQueries · cleanup worker
                ▼                                                 ▼
   Redis (cache, allkeys-lru, ec:cart:*)                SQL Server: Carts · CartItems (EnterpriseCommerce_Cart)
```

- **Layers** follow ADR-002, and the architecture tests enforce them:
  - Domain references nothing.
  - Application references the Domain only.
  - Infrastructure holds EF Core, SQL Server, StackExchange.Redis and the HTTP client.
  - Api references ASP.NET Core and the authentication block.
- **No mediator and no generic repository.** The handlers are plain classes. The store exposes one write operation: a decision run under optimistic concurrency.
- **Selective CQRS.** Commands go through the aggregate in the store, and reads use a view (`CartView`) from the cache or SQL Server.
- **No new building block.** Cart reuses `BuildingBlocks.Authentication`. It needs no service token, so the deferred token-provider block stays deferred.

## 3. Domain and data ownership

- **Cart owns:**
  - carts and their lines
  - each line's product snapshot, the quantities, the lifecycle timestamps and versions
- **Cart holds only references** to others' data:
  - Catalog's product, variant and tenant IDs, and the snapshot copied from the public read
  - the Identity subject as the owner
- **No foreign key or query reaches another service's database.** No project references Catalog or Inventory, and an architecture test enforces this.
- **The aggregate.**
  - `ShoppingCart` holds `CartItem`s and keeps the rules of ADR-029: one merchant and one currency, one line per variant, 1 to 99 units, at most 50 lines, expiry and refresh.
  - It is named `ShoppingCart` because a class `Cart` would collide with the namespace `EnterpriseCommerce.Cart`.
- **Values.**
  - `ProductSnapshot` and `Money` refuse anything outside Catalog's own bounds: name 200, SKU 64, 10 options of 40 and 100 characters, HTTPS image URL 2,048, alt text 250, price above 0 and at most 1e9 with 2 decimals, an ISO-shaped currency.
  - So a Catalog answer Cart cannot trust is never stored.
- **No domain events.** Nothing consumes them.

## 4. Database schema and migrations

| Table | Key and constraints | Indexes |
|---|---|---|
| `Carts` | PK `Id`. Checks: `Version >= 1`; merchant and currency both set or both null; currency `[A-Z]{3}` (binary collation); non-empty subject. `rowversion`. | `UX_Carts_CustomerSubject`, `IX_Carts_UpdatedAt` (cleanup) |
| `CartItems` | PK `Id`. FK to its cart, cascade on delete. Checks: quantity 1 to 99; unit price above 0 and at most 1e9 (`decimal(12,2)`); currency `[A-Z]{3}`; availability in the closed set; non-empty name and SKU. | `UX_CartItems_CartId_VariantId` |

**Notes on the schema:**

- **Options** are a small JSON array (`nvarchar(4000)`), never queried.
- **The line limit (50)** is a domain rule that no row constraint can express. Every line change also writes the cart row under its concurrency token, so two writers cannot both add the 50th line.
- **No product or variant index.** Nothing queries by them yet. A future Catalog projection would add one.
- **The migration** is `InitialCartSchema`. Its `Down` refuses with error 50200 while any cart exists (tested). The database is created with `READ_COMMITTED_SNAPSHOT` on (tested). No decision rests on a plain read: every write is checked by the concurrency token.

## 5. Security model

- **Token validation is local** (ADR-017 section 4), through `BuildingBlocks.Authentication`.
  - The only accepted audience is `ec-storefront`.
  - Merchant, admin and service tokens, an expired token, another issuer, an unknown key, `alg` HS256 or a `typ` other than `at+jwt` answer `401`.
  - A storefront token that is not a user token answers `403`.
- **Ownership.**
  - The cart is the subject's own. No request value names an owner, and the API has no cart ID.
  - Forged owner, role or tenant claims, or `X-Customer-Id` style headers, are ignored.
  - Owner fields in a body are undefined members and answer `400`.
  - Another customer's line ID answers `404`, as an unknown line does.
- **Catalog is called anonymously,** never with the customer's token.
- **Redis keys hash the subject.** No log, metric, Redis value or Catalog call carries the subject or the token (tested).
- **No secrets are committed.** Cart needs only its two connection strings.

## 6. Concurrency and cache design

- **Writes.**
  - Every command is a decision run against the customer's current cart and saved in one `SaveChanges`.
  - A concurrency-token failure, a unique-index violation (the first add of a customer, or one line of a variant) or a deadlock retries the decision on a fresh context, up to 3 attempts, with jitter.
  - The Catalog read happens once, before.
  - After the third loss the command answers `409 concurrency-conflict`, and nothing is applied.
- **`If-Match`** is optional, and `"0"` means no cart. An expired cart's visible version is 0.
- **The cache** (ADR-030): cache-aside reads and write-through after commit, both through the compare-and-set on the row's `rowversion`.
  - The circuit, the repair queue and the bypass for carts awaiting repair handle outages.
  - The connection never makes a request wait: finding F2, section 10.

## 7. Catalog integration

- **The read.** `CatalogProductReader` reads `GET /api/v1/catalog/products/{id}` on `Cart:Catalog:BaseAddress`.
  - It has a 3-second timeout, a 1 MiB cap, no retry and no token, and it propagates `X-Correlation-ID`.
- **The mapping is strict.** A `200` must be exactly the documented shape for that product, or the whole answer is refused.
- **Not published.** Only a `404` with Catalog's `product-not-found` type means "not published". Every other answer is `503 catalog-unavailable`, and nothing is written.
- **The consumer contract** is pinned on Catalog's side by `CartConsumerContractTests`, which run on MongoDB, anonymously.

## 8. Milestones

| # | Milestone | Commit |
|---|---|---|
| 1 | Decisions and contracts (ADR-029, ADR-030, the API contract) | `32ca3c9` |
| 2 | Service foundation, domain and SQL Server persistence | `06f9473` |
| 3 | Catalog product snapshots | `e2a5ed8` |
| 4 | The Catalog consumer-contract test | `0175b11` |
| 5 | The customer endpoints | `377c110` |
| 6 | The Redis cache | `ef50d68` |
| 7 | The Gateway route | `ca17c6a` |
| 8 | CI and local settings | `b318167` |
| 9 | Measurements | `519c2f2` |
| 10 | Evidence, operations and closure | the closing commit |

**Deviations from the plan:**

- **The service foundation and the domain with its persistence are one commit.** The host's readiness check needs the database context, so the foundation alone would not have been a meaningful, self-contained commit.
- **No Catalog production code changed.** The planned "smallest Catalog contract addition" was not needed (ADR-029 section 5). Only a consumer-contract test was added to Catalog.

## 9. Test strategy and evidence

**Strategy.** Correctness is tested against the real infrastructure:

- SQL Server 2022 (`mcr.microsoft.com/mssql/server:2022-CU28-ubuntu-22.04`, Compose) and Redis 8.8.4 (the Compose `redis` profile)
- no in-memory provider, and no mocked Redis
- outages are real: a refused port, a TCP relay that is cut and restored, and a real container stop and start
- Catalog is an HTTP stub that answers exactly Catalog's documented shapes, pinned on Catalog's side by a consumer-contract test on MongoDB
- concurrency tests release N callers at once and repeat in rounds

| Area | Where | What it proves |
|---|---|---|
| Domain | `Cart.UnitTests/Domain` | Creation, adding, adding again, quantities, invalid and maximum quantities, the 50-line limit, merchant and currency mismatches, removal, clearing, expiry and start-over, every refresh rule, the value bounds |
| Settings and startup | `Cart.UnitTests/Application` | Every setting's bounds; the host refuses invalid configuration in Production and accepts the Development defaults, through the startup validator without a web host (TS-3) |
| Catalog client | `Cart.UnitTests/Infrastructure` | The mapping; "not published" only for Catalog's typed `404`; 26 refused answer shapes, a transport error, a timeout within its bound, the size cap; cancellation stays cancellation; no address means an outage |
| Persistence | `Cart.IntegrationTests/Persistence` | The migration from empty; no pending model changes; the named indexes and constraints; RCSI; rollback refused with carts and allowed when empty; every constraint; a refused row rolls the whole save back; optimistic concurrency; retry exhaustion (3 attempts, nothing saved); concurrent adds (sums), concurrent first adds (one cart), clear versus add (serial), 16 customers at once; the cleanup in batches |
| API | `.../Api/CartApiTests` | Every endpoint, status and problem; the snapshot; increments; limits; mismatches with nothing changed; unavailable products and variants; five Catalog outages answering `503` with nothing written; no Catalog call for reads and quantity changes; refresh, including all or nothing; staleness; expiry and start-over; `If-Match`, including the retry of an applied add; validation and malformed bodies without parser details |
| Security | `.../Api/CartSecurityTests` | `401` on every endpoint without a token; nine rejected token kinds; `403` for non-user storefront tokens; ownership isolation; forged claims and headers ignored; no token or subject in logs; Catalog called without the customer's token |
| Host | `.../Api/HostConventionsTests`, `RequestSizeLimitTests` | Liveness without dependencies; readiness on the database only; the cache and cleanup checks; a database outage answering `503` without details; correlation echoed, in problems and propagated to Catalog; `413` on real Kestrel |
| Concurrency through the host | `.../Api/CartConcurrencyApiTests` | 12 concurrent adds of one variant add up exactly (5 rounds), with one `201`; concurrent quantity changes leave one requested value; 16 customers at once |
| Cache | `.../Caching/RedisCartCacheTests`, `CartCacheApiTests`, `CacheRestartTests` | Hit and miss; the hashed key and the TTL; an older view never replaces a newer one; the read-versus-mutation race; 32 concurrent writes in random order; expiry; foreign payloads; Redis refused (no request waits, the circuit opens); an outage through a cut relay with repair; the bounded queue; the host reading from the cache (row deleted, still served); write-through visibility; concurrent reads and writes ending with the committed cart; a flushed cache; an expired cached cart; every operation with Redis unreachable; a real container restart whose reloaded stale entry is replaced by the repair |
| Catalog contract | `Catalog.IntegrationTests/Api/CartConsumerContractTests` | The fields and types Cart snapshots, and the exact `404` problem type, on MongoDB, anonymously |
| Gateway | `DevelopmentRouteTests`, `InternalPathExposureTests` | The route's shape; forwarding of every Cart endpoint; no match for look-alike prefixes; Cart internal paths blocked on every host; seven traversal, encoded, double-encoded, backslash and case forms through the Cart route |
| Architecture | `ArchitectureTests` | Cart's layers and compiled references; the Redis client allow-listed (BFF, Cart.Infrastructure); Cart references no Catalog or Inventory project; no credentials in Cart |

**Results** (2026-11-24, Release, `EC_REQUIRE_INFRA_TESTS=true`; Compose SQL Server 2022, cache Redis 8.8.4, session Redis, Kafka 4.4.0 and MongoDB 8.0; browser tests with `EC_TEST_BROWSERS=true`). Each module ran separately with a TRX report, once, without reruns:

| Module | Baseline (end of Phase 5) | Final |
|---|---|---|
| ArchitectureTests | 222 | 317 |
| BuildingBlocks.Authentication.UnitTests | 11 | 11 |
| BuildingBlocks.Messaging.UnitTests | 11 | 11 |
| Identity.UnitTests | 169 | 169 |
| Identity.IntegrationTests | 166 | 166 |
| TenantMerchant.UnitTests | 447 | 447 |
| TenantMerchant.IntegrationTests | 219 | 219 |
| Catalog.UnitTests | 121 | 121 |
| Catalog.IntegrationTests | 137 | 139 |
| Inventory.UnitTests | 90 | 90 |
| Inventory.IntegrationTests | 128 (6 opt-in skipped) | 128 (6 opt-in skipped) |
| **Cart.UnitTests** | – | **122** |
| **Cart.IntegrationTests** | – | **116 (111 run; 5 opt-in measurements skipped)** |
| Gateway.IntegrationTests | 101 | 122 |
| Bff.UnitTests | 103 | 103 |
| Bff.IntegrationTests | 63 | 63 |
| Bff.BrowserTests | 18 | 18 |
| **Total** | **2,006** | **2,362: 2,351 passed, 0 failed, 11 skipped by design** (the opt-in measurements) |

**The other checks:**

- **Secret scan.** The CI patterns were run over the console output of every module: clean. The TRX reports also contain the synthetic `ecmi_AAAA…` theory argument of a Tenant & Merchant unit test; CI scans the console output only.
- **Documentation.** Every relative link and anchor in the repository's Markdown files was checked: no broken link.
- **A real local run** of Cart and the Gateway passed:
  - the health endpoints, `/health/cache` `Degraded` with the Redis container stopped and readiness still `Healthy`
  - `401` for an anonymous cart request, directly and through the Gateway
  - `404` for `/internal/v1/cart` and a traversal form through the Gateway

## 10. Findings during the phase

| ID | Finding | Resolution |
|---|---|---|
| F1 | **Concurrent first token signings in the test issuer produced invalid signatures** (`IDX10511`), and the broken signer stayed cached, so every later token of that issuer failed too. Found by the 16-customer concurrency test; the JWT handler's own log named the cause. The test fixture also created its host lazily without a lock, so 16 concurrent first requests had started 16 hosts. | Test infrastructure only. The Cart test issuer serializes signing, and the fixture creates its host once (`Lazy`, thread-safe). Production code signs no tokens. The issuers of the earlier services' tests have the same latent pattern: their concurrent tests sign after a warm-up (not changed). |
| F2 | **A cold or lost Redis connection could make a request wait or fail the cache at once.** With `BacklogPolicy.FailFast`, commands issued before the first connection is established fail at once and open the circuit. Measured with Redis down: the synchronous first connect blocked a request for `ConnectRetry` × `ConnectTimeout` (about 6 s with the test's 2 s). | The connection is established asynchronously, warmed up at host startup, with one connect attempt. Until the attempt is over, the cache is bypassed and no request waits (tested: under 500 ms with Redis refused). The cache tests wait for the connection explicitly before asserting what Redis holds. |
| F3 | **The first measurements' throughput varied by ten times** (for example a cache-hit run of 7 s with a p50 of 4 ms). Their callers started on a blocking `Barrier`, which held thread-pool threads, and the pool's thread injection was measured as elapsed time. | The callers start on an asynchronous gate. The rows of section 11 come from three runs after the fix. The same pattern is in the Inventory measurements; their per-request latencies are unaffected (not changed). |
| F4 | **A first `413` test raced two correct refusals**, as Catalog's TS-4 did: the body was oversized and had an undefined member. The in-memory test server also does not enforce body limits. | The `413` test runs on real Kestrel, with bodies whose only fault is their size (a declared length, and valid JSON after leading white space). |

## 11. Measurements

**Environment:**

- Windows 11, Intel Core i5-10300H (4 cores, 8 threads), 15.9 GB RAM
- Docker Desktop (WSL2): 8 CPUs, about 8.3 GB, with SQL Server 2022 CU28 (2.5 GB limit), Redis 8.8.4 (384 MB), Kafka, MongoDB and the session Redis running on the same host
- The service runs in-process on the ASP.NET Core test server (`WebApplicationFactory`), so HTTP network cost is not measured. Catalog is the in-process stub.
- Release build; `CartMeasurements`, `EC_TEST_MEASURE=true`. Carts of 5 lines for reads.

**Results.** Three runs; the ranges are over the three runs. Latencies are per request. "Refused" are `409 concurrency-conflict` answers.

| Scenario | Concurrency | Operations | Throughput | p50 ms | p95 ms | p99 ms | Refused |
|---|---|---|---|---|---|---|---|
| GET, cache hit | 16 | 1,600 | 3,892 to 3,958/s | 3.4 to 3.8 | 6.5 to 6.7 | 8.3 to 11.9 | 0 |
| GET, cache miss (the key deleted before each read; elapsed time includes the delete) | 16 | 1,600 | 732 to 997/s | 12.0 to 16.4 | 16.1 to 23.9 | 19.0 to 28.5 | 0 |
| GET, Redis unreachable (SQL Server only) | 16 | 1,600 | 910 to 1,475/s | 9.2 to 13.7 | 15.5 to 28.6 | 59.8 to 151.2 | 0 |
| POST a new line (Catalog stub read included), 16 different carts | 16 | 640 | 266 to 367/s | 42.0 to 53.3 | 61.9 to 86.4 | 74.5 to 279.3 | 0 |
| PUT quantity, 16 different carts | 16 | 640 | 244 to 309/s | 49.9 to 63.9 | 69.9 to 91.0 | 79.5 to 101.9 | 0 |
| POST the same variant, 16 different carts | 16 | 1,584 | 449 to 539/s | 28.9 to 34.9 | 39.1 to 48.7 | 48.6 to 59.6 | 0 |
| POST the same variant, **one cart, 4 callers** | 4 | 96 | 89 to 99/s | 14.1 to 14.9 | 102.9 to 103.4 | 105.7 to 139.7 | 13 to 18 (14 to 19 %) |
| POST the same variant, **one cart, 16 callers** | 16 | 96 | 118 to 149/s | 97.3 to 110.9 | 175.2 to 178.9 | 186.0 to 196.7 | 55 to 58 (57 to 60 %) |

**Observations.** These are not targets and not production expectations.

- **A cache hit** answers in about a third of the time of a SQL Server read, with 3 to 5 times the throughput.
- **Without Redis,** reads are served from SQL Server at roughly the miss rate, with a longer tail. Nothing fails.
- **Writes to different carts** do not interfere.
- **Writes to one cart serialize on its concurrency token.** Under sustained contention many requests lose all three optimistic attempts and answer `409`. Nothing is lost: every applied add is counted exactly, as the concurrency tests prove.
  - 16 callers hammering one cart is not a customer pattern.
  - Even 4 back-to-back callers on one cart, which is heavier than two tabs and a double click, see 14 to 19 % refusals.
  - **Options, if real traffic shows it,** each with its own evidence: more attempts with a longer backoff, or a short update lock on the cart row (Inventory's pattern) to queue writers instead of failing them. Neither was applied for a local benchmark.
- **Add and quantity changes** cost about 30 to 60 ms per request under 16-way load, dominated by SQL Server's write and the commit on this shared local host.

## 12. Known limitations and production gates

**Limitations:**

- **Prices and availability are as fresh as the last add or refresh.** Catalog publishes no events (ADR-024 section 13). Clients must show `pricing.stale` and call refresh, and Order must revalidate at checkout.
- **Contention on one cart** answers `409` after three attempts (section 11).
- **After a Redis outage,** a cart changed during it may be served stale by another instance for up to one TTL (15 minutes) in the worst case (ADR-030 section 4). Writes are never affected.
- **The repair queue is per instance and in memory.** It is lost on a restart, and then the TTL bound applies.
- **A refresh** reads one Catalog product per distinct product, at most 50, 4 at a time.
- **Cart trusts its clock** (`TimeProvider`) for timestamps, staleness and expiry. Instances are assumed to be time-synchronized.

**Production gates (open):**

- deployed Gateway routes and TLS, and the storefront BFF's `DownstreamApiAddress`
- a deployed Identity storefront client (the `ec-storefront` audience)
- a managed or dedicated cache Redis with TLS and its own logical database or prefix, and a licence review (ADR-006)
- a metrics exporter and the alerts of CART_OPERATIONS.md section 4
- load measurement on production-like hardware, and a decision on the contention options of section 11
- **a green CI run** (nothing is pushed)

**Deferred work (not implemented):**

- guest carts and their merge at login
- multi-merchant carts and checkout
- `Idempotency-Key` on add
- an internal cart read or hand-over for Order (the checkout phase)
- a Catalog event projection for price changes, once Catalog publishes events
- a customer-facing stock availability read
- the shared `svc-` token-provider building block (still deferred; Cart did not need it)

## 13. Completion report

- **Delivered:**
  - an independent four-layer Cart service on its own SQL Server database
  - storefront customer ownership, the single-merchant and limit rules, and indicative Catalog snapshots with explicit refresh
  - no inventory reservation
  - optimistic concurrency with server-side retries, lazy expiry and its cleanup
  - the ordered Redis cache with its outage handling
  - the Gateway route, CI support and local settings
  - the documentation and a consumer-contract test in Catalog, with no Catalog production change
- **Verified locally:** the full regression (section 9), the measurements (section 11) and a real local run.
- **Not verified:** CI (nothing is pushed), and every production gate of section 12.
