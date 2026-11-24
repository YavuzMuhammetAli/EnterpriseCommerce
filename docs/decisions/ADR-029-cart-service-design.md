# ADR-029: Cart Service Design

- **Status:** Accepted
- **Date:** 2026-11-24
- **Phase:** 6 (Cart)
- **Related:**
  - [ADR-003](ADR-003-database-per-service.md) and [ADR-011](ADR-011-per-service-data-store-assignment.md) (Cart's durable store is its own SQL Server database; Redis accelerates only)
  - [ADR-006](ADR-006-redis-usage-boundaries.md) and [ADR-030](ADR-030-cart-redis-cache-consistency.md) (the cache)
  - [ADR-009](ADR-009-selective-cqrs.md) and [ADR-010](ADR-010-no-mandatory-event-sourcing.md) (selective CQRS, no event sourcing)
  - [ADR-013](ADR-013-saga-based-workflow-coordination.md) (checkout is a later saga; no saga code now)
  - [ADR-014](ADR-014-logical-tenant-isolation.md) (a tenant ID is not proof)
  - [ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md) and [ADR-017](ADR-017-token-session-and-protocol-strategy.md) (storefront tokens, local validation)
  - [ADR-024](ADR-024-catalog-service-design.md) (Catalog owns products, variants and list prices)
  - [ADR-026](ADR-026-inventory-service-design.md) (Inventory owns stock and reservations)
- **Resolves:** for Cart, the PHASE_4_CATALOG.md section 15 row "Internal product reads for Cart and Order (snapshots)": Cart needs no internal read (section 5). The row stays open for Order.
- **Design and evidence:** [PHASE_6_CART.md](../architecture/PHASE_6_CART.md). **Contract:** [CART_API_CONTRACT.md](../api/CART_API_CONTRACT.md).

## Context

SERVICE_CATALOG section 5 gives Cart these responsibilities:

- shopping carts, cart items and the cart lifecycle
- cart validation and expiration

SQL Server is the durable store. Redis may accelerate access, and Cart must stay correct without it. The MVP checks out a cart with items from a **single merchant** (PROJECT_DECISIONS section 8). Nothing below the service boundary was designed.

Facts that constrain the design, read from the repository:

- **Customers are users of the storefront front end.**
  - A storefront user token carries `aud=ec-storefront` and `ec_ptype=user` (ADR-017 section 3, `deploy/dev/bff-dev-keys.cs`).
  - Tokens carry no roles and no tenant (ADR-017 section 2), and Identity has no "customer" classification.
  - One account may be a customer and a merchant employee at the same time (ADR-015 R6). The audience is the only signal that a request is a customer's.
  - The storefront BFF forwards `/api/*` with the session's access token to the Gateway (BFF_API_CONTRACT.md section 3.7).
  - There is no anonymous storefront token.
- **Catalog already serves what a cart needs to show, anonymously.**
  - `GET /api/v1/catalog/products/{productId}` returns a Published product only. Its fields are `id`, `tenantId` (the selling merchant), `name`, the **active** variants (`id`, `sku`, `options`, `price {amount, currency}`, `isActive`) and the media references (CATALOG_API_CONTRACT.md section 2.2).
  - An unknown, draft or archived product answers `404 product-not-found`, without telling the three apart.
  - Prices are decimals greater than 0 and at most 1,000,000,000, with at most 2 decimals, and there is one currency per product (ADR-024 section 4).
  - The read carries no version or entity tag, and Catalog publishes no events (ADR-024 section 13).
- **Catalog's other reads do not fit.** The variant verification (ADR-027) returns no price or content. The merchant API needs a merchant token.
- **Inventory has no customer-facing availability read.** Its reservations are for an allow-listed service caller, Order, which does not exist yet (ADR-026).
- **EF Core creates the database with `READ_COMMITTED_SNAPSHOT` on**, so a plain read is a snapshot read (ADR-026, Context).

## Decision

### 1. Ownership: the token's subject, storefront customers only

- **The owner of a cart is the `sub` of a storefront user token.**
  - The `customer` policy accepts a user token (`CallerClaims.UserSubjectOf`) with an allowed customer audience, `ec-storefront` by default (`Cart:Authentication:UserAudiences`).
  - Merchant, admin and service tokens have another audience and answer `401`. A token of an allowed audience that is not a user token answers `403`.
- **No request names an owner.** The API has no cart ID or customer ID in a route, a query or a body: `/api/v1/cart` is always the caller's own cart. A body member that tries to (`customerId`, `ownerId`, ...) is an unknown member and answers `400`.
  - Forged `tenant`, `role` or similar claims are ignored, because nothing reads them.
  - The Gateway does not authorize. Cart authorizes every request itself (ADR-007).
- **Guest carts are deferred.**
  - The storefront has no anonymous token (ADR-016, ADR-017), and inventing one is an authentication change.
  - A guest cart also needs a merge rule at login, which is its own design.
  - A later ADR can add guest carts without changing this model: they would be a second owner kind.

### 2. One cart per customer; no lifecycle status yet

- **A customer has at most one cart.** A unique index on `CustomerSubject` enforces it.
  - The cart is created by the first add.
  - `GET` answers `200` with an empty cart (`id: null`, `version: 0`) when none exists. No row is created by reading.
- **There is no status column.** Checkout does not exist, so states such as "checked out" would be pretend states. The checkout phase adds the lifecycle it needs by migration (for example a status, or a hand-over to Order), in its own ADR.
- **Clear** removes every item and keeps the cart row, so its version and `rowversion` keep increasing (ADR-030). It also resets the merchant and the currency.

### 3. A single merchant per cart

- **The cart's merchant** (`MerchantTenantId`) is set by its first item, from Catalog's `tenantId`, and reset to none when the cart becomes empty.
- **Adding a product of another merchant answers `409 merchant-mismatch`.** The cart is not changed: nothing is removed or replaced silently. The problem names the cart's current merchant, so the client can offer to clear the cart.
- **The currency follows the same rule.** The cart's currency is set by its first item, and adding an item priced in another currency answers `409 currency-mismatch`. Catalog allows one currency per product, not per merchant, so two products of one merchant can differ.
- **The model leaves room for a multi-merchant cart without pretending it exists.** Each item also stores its product's tenant, so a later design can group lines by merchant from the stored rows. Multi-merchant checkout and fulfillment groups stay advanced scope (PROJECT_DECISIONS section 11).

### 4. Items, quantities and limits

- **One line per variant.** The unique index `(CartId, VariantId)` enforces it.
  - Adding a variant that is already in the cart **adds to its quantity**, and refreshes its snapshot from the same Catalog read.
  - `PUT` sets a line's quantity to an absolute value.
- **Quantities are whole units** (`int`), as in Inventory (ADR-026 section 1). A fractional, missing or out-of-range quantity answers `400`.
- **Limits**, enforced by the domain and, where a row can express them, by CHECK constraints:

  | Limit | Value | Over the limit |
  |---|---|---|
  | Quantity per line | 1 to 99 | `400` for a requested value; `409 quantity-limit-exceeded` when an add would pass 99 |
  | Lines per cart | 50 | `409 cart-item-limit-exceeded` |

  The values are constants of the domain, not settings: a cart is a shopping list, not a bulk order.
- **Removing** a line deletes it. Removing the last line leaves a valid empty cart.

### 5. Catalog integration: the existing public product read

- **Cart reads products through Catalog's public `GET /api/v1/catalog/products/{productId}`.** No Catalog endpoint is added.
  - The data is public, so there is no service token, no client registration and no allow-list. It also avoids a fourth copy of the `svc-` token provider (PHASE_5_INVENTORY.md section 12).
  - Cart calls Catalog directly on `Cart:Catalog:BaseAddress`, not through the Gateway. It never reaches Catalog's database and references no Catalog project.
- **When Catalog is called:** only by the add command and by `POST /api/v1/cart/refresh`.
  - A cart read, a quantity change, a removal and a clear never call Catalog.
  - The call happens **before** the database work, so no transaction or lock waits on HTTP.
- **What the add command checks.** The answer must be a valid product with that ID. The requested variant must be one of its returned variants and active. The merchant is the answer's `tenantId`.
- **The snapshot copies only what the cart shows:**
  - the product and variant IDs, and the product's tenant
  - the product name, the SKU and the variant options (at most 10 key and value pairs)
  - the first media reference's URL and alternative text, when there is one
  - the unit price and its currency
  - `PriceCheckedAt`, the time of the Catalog read

  Descriptions, categories, brand, attributes and the other media are not copied.
- **Failures fail closed. Nothing is written without a valid answer:**

  | Catalog answer | Cart answer |
  |---|---|
  | `404` whose problem `type` is exactly Catalog's `product-not-found` | `422 product-unavailable` |
  | `200`, but the variant is missing or inactive | `422 variant-unavailable` |
  | Anything else | `503 catalog-unavailable` with `Retry-After: 5` |

  "Anything else" covers:
  - a timeout (3 seconds by default, at most 10) or a transport error
  - any other status, including a `404` without that problem type, which is how a routing error looks
  - a body that is not the documented shape, is over 1 MiB, or has another product ID
  - a price or currency outside Catalog's rules

  A transient outage never becomes a "not found". There is **no retry**: the customer's next attempt is the retry.
- **Correlation.** `X-Correlation-ID` is propagated, and `traceparent` comes from the HTTP client's diagnostics. Logs carry no product data, subject or token.

### 6. Price and availability semantics

- **A stored price is a snapshot, never a guarantee.**
  - Every line in a response carries its `priceCheckedAt`.
  - The cart carries `pricing.checkedAt` (the oldest line's) and `pricing.stale`. `stale` is true when that time is older than `Cart:Pricing:StaleAfter` (15 minutes by default) and is computed at read time.
  - The contract states that prices are indicative, and that Order takes the authoritative snapshot at checkout (PROJECT_DECISIONS section 5).
- **`POST /api/v1/cart/refresh` revalidates every line.**
  - It reads each distinct product once, at most 4 at a time.
  - It is all or nothing: if one read fails, it answers `503` and nothing changes.
  - It never removes a line. It updates the snapshots and each line's `availability`:
    - `available`: the variant is published and active; the line counts in the subtotal
    - `unavailable`: the product is no longer published, or the variant is gone or inactive
    - `currencyMismatch`: the line's new currency differs from the cart's (see below)
  - It returns the changes: `priceChanged` (with the previous price), `becameUnavailable`, `becameAvailable` and `currencyChanged`.
- **The cart's currency after a refresh.**
  - If every line with a published variant now has the same currency, that becomes the cart's currency.
  - Otherwise the cart keeps its currency, and the lines that differ are `currencyMismatch`.
- **The subtotal counts `available` lines only.** A client shows the other lines with their state, and lets the customer remove them.

### 7. No inventory in the cart

- **Cart never calls Inventory, and never reserves, holds or locks stock.** Neither a SQL lock nor a Redis lock stands in for a reservation.
- **A cart is the customer's intent, not a stock commitment.** The contract does not claim that a line is in stock or will be.
- **No availability read is used.** Inventory has none for customers, and none is invented for this phase.
- **Order reserves at checkout** and handles insufficient stock (PROJECT_DECISIONS section 8, steps 5 and 6).

### 8. Persistence and concurrency

- **Tables in Cart's own database** (`EnterpriseCommerce_Cart` locally): `Carts` and `CartItems`. Constraints and indexes are listed in PHASE_6_CART.md section 4.
- **Optimistic concurrency.**
  - Every mutation increments the cart's `Version`, its EF Core concurrency token.
  - A command is a short read-modify-write in one `SaveChanges`, which is one transaction.
  - The cart also carries a `rowversion`, read back after each save, which orders cache entries (ADR-030).
- **A conflict is retried, not returned.** A concurrency failure or a unique-index violation retries the whole database part of the command on a new context, at most 3 times, with jitter. The Catalog answer is reused, not refetched.
  - Commands are intents ("add 2", "set to 5", "remove"), so reapplying one to the newer state is correct. Two concurrent adds of a variant sum, and a clear and an add serialize in some order.
  - Concurrent first adds of one customer race on the unique subject index. The loser reloads the winner's cart and applies its command to it.
  - When the retries are exhausted, the request answers `409 concurrency-conflict` and nothing of it is applied.
- **`If-Match` is optional on every mutation.** Responses carry `ETag: "<version>"`, and `"0"` stands for "no cart". A stale tag answers `412 precondition-failed`.
  - A client that retries after an ambiguous failure, such as a timeout, can send the tag it last saw: if the first attempt was applied, the retry answers `412` instead of adding twice.
  - This is why no `Idempotency-Key` is implemented yet (deferred).
- **No long locks and no distributed locks.** The only database locks are the ones `SaveChanges` takes for its statements.

### 9. Expiry

- **A cart unchanged for `Cart:Lifecycle:InactiveCartRetention` is expired.** The default is 30 days, and the allowed range is 1 to 365 days.
  - A read of an expired cart answers an empty cart.
  - A mutation of an expired cart starts from an empty cart in the same row.
- **The cleanup worker** deletes expired carts in batches: `DELETE TOP (100)`, items by cascade, every 10 minutes with jitter, on every instance.
  - Deletion is idempotent, so the worker needs no leader.
  - `/health/cleanup` reports it, outside readiness.
- **Retention.** Personal data, the subject with its selections, is kept only for that period.

### 10. API and host

- **Endpoints** (CART_API_CONTRACT.md):
  - `GET /api/v1/cart`
  - `POST /api/v1/cart/items`
  - `PUT /api/v1/cart/items/{itemId}`
  - `DELETE /api/v1/cart/items/{itemId}`
  - `DELETE /api/v1/cart/items` (clear)
  - `POST /api/v1/cart/refresh`
- **Responses.** Every mutation answers with the whole cart and its `ETag`: `201` with `Location` for a new line, `200` otherwise.
- **The host follows the other services:**
  - RFC 9457 problems with `type` `https://errors.enterprisecommerce.example/cart/<code>`
  - `X-Correlation-ID`, and a 256 KiB body limit
  - unknown members answer `400`
  - `Cache-Control: no-store` on every cart response
  - a fail-closed fallback policy, and Serilog with path-only request logs
- **Health.**
  - `/health/live` has no dependency.
  - `/health/ready` checks SQL Server and the migrations. Neither Redis nor Catalog is part of it: Cart answers every read from SQL Server without Redis, and only add and refresh need Catalog.
- **The Gateway routes `/api/v1/cart/**`.** Cart has no internal endpoints.

## Consequences

- **Positive:** the cart is durable and owned by the token's subject only. One customer cannot name another's cart.
- **Positive:** reads, quantity changes and removals do not depend on Catalog or Redis. Only add and refresh need Catalog.
- **Positive:** no new Catalog endpoint, service client or allow-list. Cart's coupling to Catalog is one public, documented read, pinned by a contract test in Catalog.
- **Negative:** prices and availability in a cart are only as fresh as the last add or refresh. Clients must show `pricing.stale` and call refresh, and checkout must revalidate. Without Catalog events this is the honest limit.
- **Negative:** each add transfers the whole public product, including descriptions. That is bounded by Catalog's limits and by the 1 MiB cap.
- **Negative:** a refresh costs one Catalog read per distinct product, at most 50.
- **Negative:** guest carts and multi-merchant carts are not available.

## Alternatives considered

- **A new internal Catalog snapshot endpoint for a `svc-cart` client:** a smaller payload and batch reads. It needs a service client, an allow-list, a token provider and a Catalog change, while the public read already carries everything a cart shows. Rejected for now; it is a reasonable later optimization if measured refresh cost demands it.
- **Calling Catalog on every cart read:** always fresh, but every read would depend on Catalog, with N calls per read. Rejected; refresh is explicit instead.
- **Silently replacing the cart when another merchant's item is added:** surprising and lossy. Rejected for `409 merchant-mismatch`.
- **Allowing several merchants and splitting at checkout:** that is multi-merchant checkout, advanced scope. Rejected for the MVP.
- **Adding the same variant replaces the quantity:** less intuitive than adding to it. Rejected; `PUT` sets an absolute value.
- **A lifecycle status now (Active, CheckedOut):** no workflow uses it yet. Rejected; the checkout phase adds it.
- **Pessimistic locking (`UPDLOCK` on the cart row), as in Inventory:** also correct. Contention is per customer and low, and optimistic retries need no lock timeout policy. Rejected for now.
- **`Idempotency-Key` on add:** it needs a key store with retention. `If-Match` gives the retry safety a cart needs. Deferred.
- **Checking stock availability on add:** no customer read exists, and it would suggest a guarantee that the cart cannot give. Rejected.
