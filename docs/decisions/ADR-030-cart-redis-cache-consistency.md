# ADR-030: Cart's Redis Cache and Its Consistency

- **Status:** Accepted
- **Date:** 2026-11-24
- **Phase:** 6 (Cart)
- **Related:**
  - [ADR-006](ADR-006-redis-usage-boundaries.md) (Redis is never a system of record; private keys; every key has a TTL; client code in Infrastructure only)
  - [ADR-011](ADR-011-per-service-data-store-assignment.md) (Cart: SQL Server durable, Redis acceleration only; "the Cart design must define what is cached, the invalidation strategy, and behaviour when Redis is unavailable")
  - [ADR-029](ADR-029-cart-service-design.md) (the cart model and its concurrency)
- **Design and evidence:** [PHASE_6_CART.md](../architecture/PHASE_6_CART.md). **Operations:** [CART_OPERATIONS.md](../operations/CART_OPERATIONS.md).

## Context

ADR-011 requires Cart to decide three things: what Redis caches, how entries are invalidated, and what happens when Redis is unavailable. Cart must stay correct if Redis is flushed or down (ADR-006 rule 1).

A cart is read on almost every storefront page and changed far less often. Each cart has one owner, so there is no fan-out of readers over one key.

**A naive cache-aside is unsafe:**

- With "delete after commit, repopulate on miss", a read can load version 5 from SQL Server.
- A mutation then commits version 6 and deletes the key.
- The read then writes version 5 back, and the customer sees a cart they already changed until the entry expires.

Write-through with a plain `SET` has the same race between two mutations whose post-commit writes arrive out of order.

**Facts:**

- **The cache Redis already exists.** The Compose profile `redis` runs Redis 8 with `allkeys-lru` and a password (ADR-006). The dedicated `noeviction` session Redis belongs to the BFF and is never shared (ADR-016).
- **StackExchange.Redis 3.4.29 is already approved** (BFF), including Lua scripts.
- **SQL Server's `rowversion` is unique and increasing across the whole database.** Every insert or update of a row gets a value greater than any value given before in that database.

## Decision

### 1. What is cached

- **One entry per customer's cart:** the cart view that a read returns (the cart, its lines and their snapshots), without anything computed at read time. Staleness flags, totals and expiry are computed per request.
- **Not cached:**
  - the absence of a cart: a customer without a cart reads SQL Server, one index seek
  - Catalog answers
  - tokens, credentials, or the subject in clear text

### 2. Keys and values

- **Key:** `{KeyPrefix}cart:v1:{hex(SHA-256(UTF-8 subject))}`.
  - The prefix defaults to `ec:cart:` (`Cart:Cache:KeyPrefix`) and keeps the keys private to Cart (ADR-006 rule 2). Deployments may also give Cart its own logical database.
  - `v1` is the key-schema version. A change of the value's shape changes it, so old entries are never read; they simply expire.
  - The subject is hashed, so a key does not disclose it.
- **Value:** a hash with two fields:
  - `rv`: the cart row's `rowversion`, as 16 upper-case hexadecimal digits
  - `p`: the cart view as JSON, including its own schema number. An unknown schema, or a value that does not parse, is a miss.
- **TTL:** `Cart:Cache:TimeToLive`, 15 minutes by default, 1 to 60 minutes. It is set on every write. There is no key without a TTL (ADR-006 rule 3).

### 3. Ordered writes: compare-and-set on the row version

- **Every write is one Lua script that writes only forward.** It stores the value when the key has no `rv` or a lower one, and otherwise does nothing:

  ```lua
  local current = redis.call('HGET', KEYS[1], 'rv')
  if current and current >= ARGV[1] then return 0 end
  redis.call('HSET', KEYS[1], 'rv', ARGV[1], 'p', ARGV[2])
  redis.call('PEXPIRE', KEYS[1], ARGV[3])
  return 1
  ```

  Equal-length hexadecimal strings compare in numeric order.
- **A read that missed** writes what it read from SQL Server (cache-aside).
- **A committed mutation** writes its new state after the commit (write-through). It never deletes the key.
- **Consequences of the ordering:**
  - A slow read that loaded an older version can never replace a newer entry. It finds a higher `rv` and does nothing.
  - Two mutations whose post-commit writes arrive out of order leave the newer one.
  - `rowversion` is database-wide, so a cart that was deleted (expiry) and recreated still has a higher version than any entry of the old cart.
- **Expiry is checked on a hit too.** An entry whose `updatedAt` is older than the retention (ADR-029 section 9) is treated as a miss, so a cart the cleanup worker deleted cannot be served from the cache.
- **No distributed lock and no stampede protection.** Each key has one owner, so concurrent misses on one key are rare and harmless: the compare-and-set orders them.

### 4. Failure behaviour

- **Redis is never on the correctness path.** Every cache call catches its failure, logs it without the subject, counts it and continues:
  - A read falls back to SQL Server.
  - A committed mutation still answers success. A failed cache write never fails or rolls back a committed change.
- **Short bounds.** Operations time out after 250 ms (`Cart:Cache:OperationTimeout`) and connecting after 1 second. Commands are not queued while disconnected (`BacklogPolicy.FailFast`), and the client reconnects in the background (`AbortOnConnectFail=false`).
- **A circuit per instance.** After a failure, the instance bypasses Redis for 5 seconds (`Cart:Cache:CircuitBreakDuration`), so an outage costs one timeout per period instead of one per request.
- **A bounded repair queue for missed post-commit writes.**
  - A post-commit write that fails or is bypassed is kept in a local queue: one entry per cart, the newest view, at most 1,000 entries (`Cart:Cache:RepairQueueCapacity`).
  - A background loop replays the compare-and-set once Redis answers again. Replaying is safe in any order, because it only writes forward.
  - An entry older than the TTL is dropped: any entry it would have corrected has expired anyway. A full queue drops new entries and counts them.
- **The guarantee:**
  - While Redis is healthy, a read after a committed mutation returns that mutation or a newer one, on every instance.
  - After an outage, a cart changed during it can be served stale only until the changing instance's repair runs. In the worst case, for example when that instance stopped during the outage, it can be stale until its entry's TTL ends, 15 minutes by default.
  - Writes always load the cart from SQL Server under the optimistic concurrency of ADR-029 section 8. A stale cached view can therefore show an old cart, but it can never cause a lost or wrong update.
  - A mutation's response is always read from SQL Server's committed state, never from the cache.
- **Flushing Redis** only costs the cache: the next reads repopulate it.

### 5. Configuration and health

- **Configuration.**
  - `Cart:Cache:Enabled` is true by default. When true, `ConnectionStrings:CartCache` is required, and a missing value fails at startup.
  - `Enabled=false` runs Cart on SQL Server alone, for example in a deployment without Redis or in tests.
- **Health.**
  - `/health/cache` pings Redis and reports `Degraded` when it fails.
  - It is **not** part of readiness: Cart serves every request without Redis.
- **Meters** (`EnterpriseCommerce.Cart`, `outcome` tags from closed sets, never a key or subject):
  - `cart.cache.reads`: `hit`, `miss`, `expired`, `bypassed`, `error`
  - `cart.cache.writes`: `stored`, `superseded`, `bypassed`, `error`
  - `cart.cache.repairs`: `queued`, `repaired`, `dropped`

## Consequences

- **Positive:** a cache hit avoids the SQL Server round trips of a read, with no window in which a delete-then-repopulate race can resurrect old data.
- **Positive:** Redis loss, flush or eviction (`allkeys-lru`) changes latency only, never results or writes.
- **Negative:** one Lua call per miss and per mutation. The cache adds a hop on a miss (measured in PHASE_6_CART.md).
- **Negative:** bounded staleness after an outage: until the repair runs, at most one TTL. Documented in the contract.
- **Negative:** the repair queue is per instance and in memory. It is lost on a restart, and then the TTL bound applies.

## Alternatives considered

- **Delete after commit, repopulate on miss:** has exactly the race described above. Rejected.
- **Plain write-through `SET`:** reorders between two mutations. Rejected.
- **A per-cart `Version` as the order:** it restarts when a cart is deleted and recreated, so an old cart's entry could look newer. Rejected for the database-wide `rowversion`.
- **Checking SQL Server's version on every hit:** always fresh, but it costs a round trip per read and removes most of the benefit. Rejected.
- **A distributed lock around read-and-populate:** adds latency and a failure mode for a problem the compare-and-set already solves. Rejected (ADR-006: locks only with documented failure modes, and not needed here).
- **Redis as the store with SQL Server written behind:** makes Redis a system of record. Forbidden by ADR-006 and ADR-011.
- **`IDistributedCache`:** cannot express the compare-and-set. Rejected, as for the BFF.
- **Using the BFF's session Redis:** it is `noeviction` and dedicated to sessions (ADR-016). Rejected; Cart uses the cache Redis.
