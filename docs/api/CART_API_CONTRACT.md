# Cart API Contract

This is the HTTP API of the Cart service. The cross-service conventions (problem details, `X-Correlation-ID`, `ETag` and `If-Match`, the body limit) are in [README.md](README.md).

- **Decisions:**
  - [ADR-029](../decisions/ADR-029-cart-service-design.md) (design)
  - [ADR-030](../decisions/ADR-030-cart-redis-cache-consistency.md) (cache)
- **Operations:** [CART_OPERATIONS.md](../operations/CART_OPERATIONS.md).
- **Catalog dependency:** the public product read, [CATALOG_API_CONTRACT.md section 2.2](CATALOG_API_CONTRACT.md#22-read-a-published-product).

## 1. Summary

| Endpoint | Caller | Section |
|---|---|---|
| `GET /api/v1/cart` | Storefront customer | [3](#3-read-the-cart) |
| `POST /api/v1/cart/items` | Storefront customer | [4](#4-add-an-item) |
| `PUT /api/v1/cart/items/{itemId}` | Storefront customer | [5](#5-change-a-quantity) |
| `DELETE /api/v1/cart/items/{itemId}` | Storefront customer | [6](#6-remove-an-item) |
| `DELETE /api/v1/cart/items` | Storefront customer | [7](#7-clear-the-cart) |
| `POST /api/v1/cart/refresh` | Storefront customer | [8](#8-refresh-prices-and-availability) |
| `GET /health`, `/health/live`, `/health/ready`, `/health/cache`, `/health/cleanup` | Anonymous | [CART_OPERATIONS.md section 3](../operations/CART_OPERATIONS.md#3-health) |

Rules for every endpoint:

- **The cart is always the caller's own.** It is identified by the token's `sub` only. No route, query or body names a cart owner or a customer.
- **A cart is the customer's intent, not a stock commitment.**
  - Adding an item reserves no stock, and a line in a cart may be out of stock at checkout.
  - Prices in a cart are **indicative snapshots** from Catalog. They are not guaranteed: Order takes the authoritative prices and reserves stock at checkout.
- **Request bodies** are JSON of at most 256 KiB (`413` above it).
  - A member that the contract does not define answers `400`, and so does malformed JSON.
  - Quantities are whole numbers, so a fractional quantity also answers `400`.
- **Responses.** Every cart response carries `ETag: "<version>"` and `Cache-Control: no-store`. A customer without a cart has version `0`.
- **`If-Match` is optional on every mutation.** It must be exactly one strong tag. A tag other than the cart's current version answers `412 precondition-failed`, and so does a malformed, weak or wildcard tag. `"0"` means "only if I have no cart yet".
- **`X-Correlation-ID`** is optional: 1 to 64 of `A-Z a-z 0-9 - _ . :`, and otherwise a new one is generated. It is echoed on the response, added to every problem and propagated to Catalog.
- **Errors** are RFC 9457 problem details with `type` `https://errors.enterprisecommerce.example/cart/<code>` (section 9).
- **Redis is never visible.** Whether the cache is up changes latency only, never a status or a body.

## 2. Authorization

Every endpoint needs a **storefront user token**:

- audience `ec-storefront` (`Cart:Authentication:UserAudiences`)
- `ec_ptype=user`
- a non-empty `sub`
- a client that is not a `svc-` client

What is refused:

- **`401`:** no token, another audience (for example a merchant-portal `ec-merchant` token or a service token), another issuer, an unknown signing key, an expired token, or a token that is not `at+jwt`.
- **`403`:** a token of an allowed audience that is not a user token.
- **Ignored:** tenant, role or customer claims a token might carry. The Gateway does not authorize; Cart does, on every request.

## 3. Read the cart

`GET /api/v1/cart` answers `200`.

```json
{
  "id": "0193b6b5-6b9e-7c3a-9d58-3c1f5b2a7e10",
  "version": 4,
  "merchantTenantId": "0193b6a0-11aa-7bbb-8ccc-0d0e0f101112",
  "currency": "EUR",
  "items": [
    {
      "id": "0193b6b5-7000-7a00-8b00-000000000001",
      "productId": "0193b6a2-2222-7333-8444-555566667777",
      "variantId": "0193b6a2-2222-7333-8444-555566668888",
      "name": "Blue shirt",
      "sku": "BS-M",
      "options": [ { "key": "size", "value": "M" } ],
      "image": { "url": "https://cdn.example.com/a.jpg", "altText": "Front" },
      "quantity": 2,
      "unitPrice": { "amount": 19.99, "currency": "EUR" },
      "lineTotal": { "amount": 39.98, "currency": "EUR" },
      "availability": "available",
      "priceCheckedAt": "2026-11-24T10:00:00+00:00",
      "addedAt": "2026-11-24T10:00:00+00:00",
      "updatedAt": "2026-11-24T10:05:00+00:00"
    }
  ],
  "lineCount": 1,
  "totalQuantity": 2,
  "subtotal": { "amount": 39.98, "currency": "EUR" },
  "pricing": { "checkedAt": "2026-11-24T10:00:00+00:00", "stale": false },
  "createdAt": "2026-11-24T10:00:00+00:00",
  "updatedAt": "2026-11-24T10:05:00+00:00",
  "expiresAt": "2026-12-24T10:05:00+00:00"
}
```

**Fields:**

| Field | Meaning |
|---|---|
| `merchantTenantId` | The single merchant of every line. `null` when the cart is empty. |
| `currency` | The single currency of every line. `null` when the cart is empty. |
| `image` | The product's first media reference, or `null`. |
| `availability` | `available`, `unavailable` (no longer published, or the variant is gone or inactive), or `currencyMismatch` (after a refresh, the line is priced in another currency than the cart). The set is open, so a client treats an unknown value as not available. |
| `lineTotal` | `unitPrice` × `quantity`. |
| `subtotal` | The sum of the `available` lines, or `null` when no line is available. |
| `pricing.checkedAt` | The oldest `priceCheckedAt` of the lines. `null` when the cart is empty. |
| `pricing.stale` | `true` when `pricing.checkedAt` is older than 15 minutes (`Cart:Pricing:StaleAfter`). Call refresh (section 8) before showing prices as current. |
| `expiresAt` | When the cart expires if it is not changed (`updatedAt` + 30 days, `Cart:Lifecycle:InactiveCartRetention`). An expired cart reads as empty. |

**Without a cart**, and for an expired cart, the answer is `200` with `ETag: "0"`:

```json
{
  "id": null, "version": 0, "merchantTenantId": null, "currency": null, "items": [],
  "lineCount": 0, "totalQuantity": 0, "subtotal": null, "pricing": null,
  "createdAt": null, "updatedAt": null, "expiresAt": null
}
```

Reading never calls Catalog and never creates a cart.

## 4. Add an item

`POST /api/v1/cart/items`

```json
{ "productId": "<guid>", "variantId": "<guid>", "quantity": 2 }
```

**Validation:**

- `productId` and `variantId` are required, non-empty GUIDs.
- `quantity` is required, a whole number from 1 to 99.

**Behaviour:**

1. **Cart reads the product from Catalog** (ADR-029 section 5). It does so before any database work.
2. **A new variant becomes a new line**, with the snapshot of the Catalog answer. The answer is `201` with `Location: /api/v1/cart/items/{itemId}` and the cart.
   - The first line sets the cart's merchant and currency.
   - The cart is created if the customer has none.
3. **A variant that is already in the cart adds `quantity` to its line**, and its snapshot is refreshed from the same answer. The answer is `200` with the cart.

**Refused, and nothing changes:**

| Status | Code | When |
|---|---|---|
| `400` | `validation-failed` | an invalid member |
| `409` | `merchant-mismatch` | the product belongs to another merchant than the cart's. The problem has `cartMerchantTenantId`. |
| `409` | `currency-mismatch` | the variant is priced in another currency than the cart's. The problem has `cartCurrency`. |
| `409` | `quantity-limit-exceeded` | the line would exceed 99 |
| `409` | `cart-item-limit-exceeded` | the cart already has 50 lines |
| `412` | `precondition-failed` | a stale `If-Match` |
| `422` | `product-unavailable` | Catalog knows no **published** product with that ID: unknown, draft and archived are not told apart |
| `422` | `variant-unavailable` | the product is published but has no active variant with that ID |
| `503` | `catalog-unavailable` | Catalog gave no usable answer: a timeout, an error status, an unexpected body, or no Catalog configured (Development only). Comes with `Retry-After: 5`. |

## 5. Change a quantity

`PUT /api/v1/cart/items/{itemId}` with `{ "quantity": 5 }`. `quantity` is a whole number from 1 to 99; use section 6 to remove a line.

- **The quantity is set, not added.** The answer is `200` with the cart.
- **Catalog is not called.** The snapshot and the availability stay as they were.
- **Errors:**
  - `404 cart-item-not-found`: no such line in the caller's cart
  - `400 validation-failed`
  - `412 precondition-failed`

## 6. Remove an item

`DELETE /api/v1/cart/items/{itemId}` answers `200` with the cart.

- Removing the last line leaves an empty cart, and resets its merchant and currency.
- **Errors:** `404 cart-item-not-found`, `412 precondition-failed`.

## 7. Clear the cart

`DELETE /api/v1/cart/items` removes every line and answers `200` with the (empty) cart.

- Clearing an empty cart, or a customer without a cart, also answers `200`, and changes nothing.
- **Error:** `412 precondition-failed`.

## 8. Refresh prices and availability

`POST /api/v1/cart/refresh` takes no body. It re-reads every distinct product of the cart from Catalog and answers `200`:

```json
{
  "cart": { "...": "the cart, as in section 3" },
  "changes": [
    {
      "itemId": "0193b6b5-7000-7a00-8b00-000000000001",
      "kind": "priceChanged",
      "previousUnitPrice": { "amount": 19.99, "currency": "EUR" },
      "unitPrice": { "amount": 17.99, "currency": "EUR" }
    },
    { "itemId": "0193b6b5-7000-7a00-8b00-000000000002", "kind": "becameUnavailable" }
  ]
}
```

- **`kind`** is one of `priceChanged`, `currencyChanged`, `becameUnavailable` or `becameAvailable`. The set is open.
- **A refresh never removes a line.** Name, SKU, options, image and prices are updated for every published variant, and `priceCheckedAt` becomes the time of the read.
- **The cart's currency:** when every line with a published variant shares one currency, that becomes the cart's currency. Otherwise the lines that differ become `currencyMismatch`.
- **All or nothing.** If any Catalog read fails, the answer is `503 catalog-unavailable` and nothing changes.
- **An empty cart** answers `200` with no changes.
- **Error:** `412 precondition-failed`.

## 9. Problems

| Status | Code | Meaning |
|---|---|---|
| `400` | `validation-failed` | A member is invalid; `errors` names it. |
| `400` | (RFC 9110 `#section-15.5.1`) | The body is malformed, has an unknown member, or a value of the wrong type. |
| `401` | (empty body) | No valid token (section 2). |
| `403` | (empty body) | A token of an allowed audience that is not a customer's. |
| `404` | `cart-item-not-found` | No such line in the caller's cart. |
| `409` | `merchant-mismatch`, `currency-mismatch`, `quantity-limit-exceeded`, `cart-item-limit-exceeded` | Section 4. |
| `409` | `concurrency-conflict` | Concurrent changes to the same cart kept winning after the server's retries. Read the cart and try again. |
| `412` | `precondition-failed` | `If-Match` does not match the cart's current version. |
| `413` | (RFC 9110 `#section-15.5.14`) | The body is over 256 KiB. |
| `422` | `product-unavailable`, `variant-unavailable` | Section 4. |
| `503` | `catalog-unavailable` | Catalog gave no usable answer. Comes with `Retry-After: 5`. |
| `503` | (RFC 9110 `#section-15.6.4`) | The Cart database is unavailable. Comes with `Retry-After: 5`. |

Every problem has `type`, `title`, `status`, `instance` (`"<METHOD> <path>"`), `traceId` and `correlationId`. No problem contains exception messages, SQL, Redis or Catalog details.

## 10. Not provided

- **Guest carts, and the merge of a guest cart at login** (ADR-029 section 1).
- **Several merchants in one cart, and several carts per customer** (ADR-029 sections 2 and 3).
- **Stock availability and reservations:** Order reserves at checkout (ADR-029 section 7).
- **Checkout, orders and payments:** later phases.
- **`Idempotency-Key`:** `If-Match` covers retries (ADR-029 section 8).
- **An internal cart read for Order:** decided in the checkout phase.
