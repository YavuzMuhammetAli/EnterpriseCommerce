# Cart Operations

This is the runbook of the Cart service. It covers:

- local start and configuration
- health, metrics and logs
- the cache and its outages
- migrations and rollback
- the expired-cart cleanup
- failure handling

**References:**

- **Decisions:** [ADR-029](../decisions/ADR-029-cart-service-design.md) (design), [ADR-030](../decisions/ADR-030-cart-redis-cache-consistency.md) (cache)
- **Contract:** [CART_API_CONTRACT.md](../api/CART_API_CONTRACT.md)
- **Design and evidence:** [PHASE_6_CART.md](../architecture/PHASE_6_CART.md)
- **Code:** `src/Services/Cart`

## 1. Local start

1. **Start the infrastructure:** `docker compose --profile sqlserver --profile redis up -d --wait`.
   - These are the existing SQL Server and cache Redis.
   - Cart adds no container.
2. **Start Identity and Catalog** (CLAUDE.md, Commands). Cart validates storefront tokens issued by Identity, and reads Catalog's public product read on `https://localhost:7103/`, the Development default.
3. **Start Cart:**

   ```powershell
   $env:ConnectionStrings__CartDatabase = 'Server=127.0.0.1,1433;Database=EnterpriseCommerce_Cart;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=true'
   $env:ConnectionStrings__CartCache = '127.0.0.1:6379,password=<REDIS_PASSWORD>'
   $env:Cart__Database__ApplyMigrationsAtStartup = 'true'
   dotnet run --project src/Services/Cart/EnterpriseCommerce.Cart.Api --launch-profile https   # https://localhost:7105
   ```

4. **Through the Gateway:** `http://localhost:5000` (or `https://localhost:7000`) routes `/api/v1/cart/**` on the host `localhost:7000`.

   A browser reaches Cart through the storefront BFF, whose `/api/**` forwarding attaches the session's `ec-storefront` access token (BFF_API_CONTRACT.md section 3.7). For this, set `Bff__DownstreamApiAddress` to the Gateway.

**Without some of the dependencies** (Development only):

- **Without Redis,** or with `Cart__Cache__Enabled=false`, every request is served from SQL Server.
- **Without Catalog,** adds and refreshes answer `503 catalog-unavailable`. Every other request still works.

**No secrets are needed beyond the connection strings.** Cart has no service client, no key and no allow-list: it accepts storefront user tokens only, and calls Catalog anonymously.

## 2. Configuration

| Setting | Default | Allowed and meaning |
|---|---|---|
| `ConnectionStrings:CartDatabase` | none | Required. Environment or user secrets, never committed. |
| `Cart:Database:ApplyMigrationsAtStartup` | `false` | Development only. Deployed environments migrate as a separate step (section 6). |
| `Cart:Authentication:Issuer` | none (Development: `https://localhost:7101/`) | Required, matched exactly against `iss`. HTTPS outside Development. |
| `Cart:Authentication:MetadataAddress` | the issuer's discovery document | Optional internal address of Identity's metadata |
| `Cart:Authentication:UserAudiences` | `[ec-storefront]` | The customer front ends whose user tokens are accepted |
| `Cart:Catalog:BaseAddress` | none (Development: `https://localhost:7103/`) | Catalog's own address, ending with `/`. Required and HTTPS outside Development. |
| `Cart:Catalog:Timeout` | 3 s | 100 ms to 10 s, per product read |
| `Cart:Catalog:MaxResponseBytes` | 1 MiB | 64 KiB to 8 MiB |
| `ConnectionStrings:CartCache` | none | Required when the cache is enabled. Never committed. |
| `Cart:Cache:Enabled` | `true` | `false` runs on SQL Server alone |
| `Cart:Cache:KeyPrefix` | `ec:cart:` | 1 to 64 characters without white space |
| `Cart:Cache:TimeToLive` | 15 min | 1 to 60 min. The worst-case staleness after an outage (ADR-030 section 4). |
| `Cart:Cache:OperationTimeout`, `ConnectTimeout` | 250 ms, 1 s | 50 ms to 2 s, and 100 ms to 10 s |
| `Cart:Cache:CircuitBreakDuration` | 5 s | 1 to 60 s |
| `Cart:Cache:RepairQueueCapacity`, `RepairInterval` | 1,000, 1 s | 0 to 100,000 (0: no repair, TTL bound only), and 100 ms to 1 min |
| `Cart:Lifecycle:InactiveCartRetention` | 30 days | 1 to 365 days |
| `Cart:Pricing:StaleAfter` | 15 min | 1 min to 24 h |
| `Cart:Cleanup:Enabled`, `Interval`, `BatchSize`, `MaxBatchesPerCycle` | `true`, 10 min, 100, 20 | 10 s to 24 h; 1 to 1,000; 1 to 100 |

**Invalid settings stop the service at startup** (`ValidateOnStart`, and the security settings at host construction). The error names the section and this document.

**The limits of a cart** (99 units per line, 50 lines) are domain constants, not settings (ADR-029 section 4).

## 3. Health

The responses carry the status word only. `Degraded` answers `200`. Every check gives up after 5 seconds.

| Endpoint | Checks | Fails when |
|---|---|---|
| `/health`, `/health/live` | the process only | never, while the process runs |
| `/health/ready` | SQL Server is reachable and has every migration of this build | the database is unreachable, or a migration is pending (`Unhealthy`, `503`) |
| `/health/cache` | Redis answers a ping (`Healthy` when the cache is disabled) | Redis is unavailable (`Degraded`) |
| `/health/cleanup` | the cleanup completed a cycle within three intervals | the cleanup is behind (`Degraded`) |

**Why readiness checks only SQL Server.**

- **Redis is not part of readiness.** Every request is served from SQL Server without it (ADR-030).
- **Catalog is not part of readiness** either. Only add and refresh need it, and they fail closed with `503 catalog-unavailable` on their own. Taking the whole API out of rotation for a Catalog outage would also stop reads, quantity changes and removals that do not need Catalog.
- **Catalog's state** is observable through `cart.catalog_reads` (section 4).

## 4. Metrics and logs

**Meter `EnterpriseCommerce.Cart`.** Tags come from closed sets and never carry a customer, cart, item, product, variant, merchant or key.

| Instrument | Tags |
|---|---|
| `cart.commands` | `command`: `add`, `set_quantity`, `remove`, `clear`, `refresh`; `outcome`: `applied`, `unchanged`, `refused`, `conflict` |
| `cart.catalog_reads` | `outcome`: `found`, `not_published`, `failed` |
| `cart.cache.reads` | `outcome`: `hit`, `miss`, `expired`, `bypassed`, `error` |
| `cart.cache.writes` | `outcome`: `stored`, `superseded`, `bypassed`, `error` |
| `cart.cache.repairs` | `outcome`: `queued`, `repaired`, `dropped` |
| `cart.concurrency_retries` | none |
| `cart.carts_expired` | none (the count deleted by the cleanup) |

No exporter is connected yet (observability phase).

**Alerts to define with the exporter:**

- `cart.catalog_reads{outcome=failed}` rising: Catalog is unreachable
- `cart.cache.reads{outcome=error|bypassed}` sustained: a Redis outage
- `cart.cache.repairs{outcome=dropped}` above zero: an outage longer than the queue or the TTL
- `cart.commands{outcome=conflict}`: contention on carts

**Logs** are structured with Serilog (compact JSON on the console):

- **Request logs** carry the method, the path, the status and the duration, never the query string, headers or bodies.
- **No log contains** a token, a customer subject, a product, a cart's contents or a Redis key. The Catalog and cache logs carry the failure kind and status code only.

| Event ID | Level | Meaning |
|---|---|---|
| 5000 | Error | Unhandled exception (`500`) |
| 5001 | Error | The database was unavailable (`503`) |
| 5002 | Error | Catalog was unavailable (`503 catalog-unavailable`) |
| 5030, 5031 | Information, Warning | A write lost a concurrency race and was retried; or lost every attempt (`409`) |
| 5040, 5041 | Information, Warning | The cleanup deleted expired carts; a cleanup cycle failed |
| 5210 | Warning | No usable Catalog answer (the failure kind and status) |
| 5300, 5301, 5302, 5303 | Warning, Warning, Warning, Information | A cache read, write or repair failed; Redis is unreachable at startup |

## 5. Failure handling

| Failure | Behaviour | Action |
|---|---|---|
| **SQL Server unavailable** | Every cart request answers `503` with `Retry-After: 5`, and nothing is reported as done. Readiness fails. Liveness stays healthy. | Restore the database. Clients retry. |
| **Redis unavailable, flushed or evicting** | Nothing changes for clients except latency: reads come from SQL Server, writes are committed. The instance bypasses Redis for 5 s after each failure, and missed post-commit writes wait in the repair queue. | Restore Redis. No data needs recovery. The repair replays committed views once Redis answers. |
| **Redis back after an outage** | A cart changed during the outage may be served stale by another instance until its entry is replaced: by the repair of the instance that changed it, within seconds, or in the worst case by the TTL (15 min, ADR-030 section 4). Writes are never affected. | None. To end any staleness at once, delete the keys `ec:cart:cart:v1:*`. |
| **Catalog unavailable or answering garbage** | Adds and refreshes answer `503 catalog-unavailable` with `Retry-After: 5`, and nothing is written. Reads, quantity changes, removals and clears are unaffected. | Restore Catalog. Check `Cart:Catalog:BaseAddress`: a misrouted address answers 404 without Catalog's problem type, which is treated as an outage. |
| **Many `409 concurrency-conflict`** | Concurrent writes to one cart lost three optimistic attempts ([PHASE_6_CART.md section 11](../architecture/PHASE_6_CART.md#11-measurements) has the measured rates). Nothing of the refused request was applied. | Clients read the cart and retry. |
| **The cleanup is behind** | `/health/cleanup` is `Degraded`. Expired carts still read as empty. They only take space longer. | Check the database. The worker retries every interval. |

**Clearing the cache** is always safe, for example after a configuration error: delete the keys under the prefix (`SCAN` and `DEL`, or `UNLINK`). Never use `FLUSHALL` on a shared cache Redis.

## 6. Migrations

**The migration is `InitialCartSchema`:**

- `Carts`, with the unique customer index, the `UpdatedAt` index, the `rowversion`, and checks for the version, the merchant and currency pair, the currency shape and the subject
- `CartItems`, with the unique (cart, variant) index, the cascade from its cart, and checks for the quantity, price, currency, availability, name and SKU

**Applying it:**

- **Locally:** `Cart__Database__ApplyMigrationsAtStartup=true` (Development only).
- **Deployed:** run it as a separate step:

  ```bash
  dotnet tool run dotnet-ef migrations script --idempotent --project src/Services/Cart/EnterpriseCommerce.Cart.Infrastructure --startup-project src/Services/Cart/EnterpriseCommerce.Cart.Infrastructure
  ```

  Review the output, then apply it. A service started on a database behind its code is not ready (section 3).

**Rolling back:**

- The `Down` of `InitialCartSchema` refuses with error 50200 while any cart exists. Customers' carts are never discarded by a migration.
- Rolling back an empty database is allowed (tested).
- A later rollback of a populated database is a data decision. It is not a migration step.

**New migrations:** the command is in CLAUDE.md. Move the snapshot into `Persistence/Migrations`.

## 7. The expired-cart cleanup

- **What it deletes.** Every instance deletes carts unchanged for `Cart:Lifecycle:InactiveCartRetention` (30 days), every `Cart:Cleanup:Interval` (10 min, with jitter).
  - Each statement deletes `TOP (100)` carts with `UpdatedAt` at or before the cutoff, and their lines by cascade.
  - A cycle runs at most 20 statements.
- **Safety.**
  - Deletion is idempotent, so the instances need no leader.
  - The condition is part of the statement, so a cart changed after the cutoff is never deleted.
  - A request that changes a cart while it is deleted loses its optimistic race and retries, on no cart, as a first add.
- **A cached copy of a deleted cart** is not served: reads check expiry on cached entries too. It expires with its TTL.
- **Effect on reads.** Expired carts read as empty whether or not they were deleted yet. The cleanup only bounds storage and the retention of personal data.
