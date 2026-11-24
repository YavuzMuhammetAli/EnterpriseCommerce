# Inventory API Contract

The HTTP API of the Inventory service. The cross-service conventions (problem details, `X-Correlation-ID`, `Idempotency-Key`, paging) are in [README.md](README.md).

- **Decisions:**
  - [ADR-026](../decisions/ADR-026-inventory-service-design.md) (design)
  - [ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md) (merchant authorization)
  - [ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md) (Catalog verification)
  - [ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) (events)
- **Events:** [INVENTORY_EVENTS.md](../events/INVENTORY_EVENTS.md). **Operations:** [INVENTORY_OPERATIONS.md](../operations/INVENTORY_OPERATIONS.md).

## 1. Summary

| Endpoint | Caller | Section |
|---|---|---|
| `POST /api/v1/inventory/tenants/{tenantId}/items` | Merchant member (`ManageInventory`: Owner, Admin) | [3](#3-create-an-inventory-item) |
| `GET /api/v1/inventory/tenants/{tenantId}/items` | Merchant member (`ViewInventory`: any role) | [4](#4-list-and-read-items) |
| `GET /api/v1/inventory/tenants/{tenantId}/items/{itemId}` | Merchant member (`ViewInventory`) | [4](#4-list-and-read-items) |
| `POST /api/v1/inventory/tenants/{tenantId}/items/{itemId}/adjustments` | Merchant member (`ManageInventory`) | [5](#5-adjust-stock) |
| `GET /api/v1/inventory/tenants/{tenantId}/items/{itemId}/movements` | Merchant member (`ViewInventory`) | [6](#6-read-the-movement-history) |
| `/internal/v1/inventory/tenants/{tenantId}/reservations` (create, read, commit, release) | Allow-listed service client (scope `svc.inventory.reservations`) | [7](#7-internal-reservations) |
| `GET /health`, `/health/live`, `/health/ready`, `/health/outbox`, `/health/expiry` | Anonymous | [INVENTORY_OPERATIONS.md section 3](../operations/INVENTORY_OPERATIONS.md#3-health) |

**Rules for every endpoint:**

- **The tenant comes from the route only, and is never proof** (ADR-014 rule 4).
  - Every merchant request is authorized by Tenant & Merchant's verdict for the token's subject, on every request, never cached.
  - Every read and write is scoped by the route's tenant: another tenant's item or reservation answers `404`.
- **Request bodies** are JSON of at most 256 KiB.
  - A member the contract does not define answers `400`.
  - **Quantities are whole numbers**; a fractional number answers `400` (ADR-026 section 1).
- **`X-Correlation-ID`** is optional (1 to 64 of `A-Z a-z 0-9 - _ . :`; otherwise a new one is generated).
  - It is echoed on the response and added to every problem.
  - It is propagated to Tenant & Merchant and Catalog, and stored on every movement and integration event.
- **Errors** are RFC 9457 problem details with `type` `https://errors.enterprisecommerce.example/inventory/<code>` (section 9).

## 2. Authorization

**Merchant endpoints** need a user token of the merchant portal:

- audience `ec-merchant`
- `ec_ptype=user`
- a client that is not a `svc-` client

A service token answers `403`. The permission comes from the role in Tenant & Merchant's verdict (ADR-026 section 10):

| Permission | `MerchantOwner` | `MerchantAdmin` | `MerchantStaff` |
|---|---|---|---|
| `ViewInventory` (items, balances, availability, movements) | yes | yes | yes |
| `ManageInventory` (create items, adjust stock) | yes | yes | no |

- **No active membership** in the tenant, or no such tenant: `404 tenant-not-found`.
- **An insufficient role:** `403 permission-denied`.
- **An unknown role** grants nothing.
- **Forged tenant or role claims** in a token are ignored.
- **No verdict** (Tenant & Merchant unreachable, a timeout, an unexpected answer, no service token, or no integration configured, which is allowed in Development only): `503 membership-unavailable` with `Retry-After: 5`, and nothing is read or written.

**Token failures** answer `401`: no token, another audience, another issuer, an unknown signing key, an expired token, or a token that is not `at+jwt`.

## 3. Create an inventory item

`POST /api/v1/inventory/tenants/{tenantId}/items` with an `Idempotency-Key` header:

```json
{ "productId": "<guid>", "variantId": "<guid>", "initialOnHand": 25 }
```

- **Validation.** `initialOnHand` is between 0 and 1,000,000,000.
- **Catalog verification.** Inventory asks Catalog's variant verification (ADR-027) whether the variant belongs to that product of the tenant. It does so before the transaction, so no lock is held during the call. The product must be `Draft` or `Published`; the variant may be inactive. The SKU is recorded as a snapshot.
- **The write.** The item, its `InitialStock` movement and its `StockLevelChanged` event commit in one transaction.

**Answers:**

| Status | When |
|---|---|
| `201` | Created. `Location` points to the item, and the body is the item (section 4). |
| `200` | The same key and body again: the original item, in its current state |
| `400 validation-failed` | A missing or invalid field, or a missing or malformed `Idempotency-Key` |
| `409 inventory-item-exists` | The tenant already tracks this variant (one item per tenant and variant) |
| `409 variant-not-eligible` | The product is archived, or its status is unknown (fail closed), or Catalog's SKU is not a valid SKU |
| `422 variant-not-found` | Catalog has no such variant in that product of the tenant |
| `422 idempotency-key-reused` | The key was used for a different body |
| `503 catalog-unavailable` | No Catalog verdict; nothing is written |

**Idempotency scope:** (tenant, user subject, key).

## 4. List and read items

**The item representation:**

```json
{
  "id": "<guid>", "tenantId": "<guid>", "productId": "<guid>", "variantId": "<guid>", "sku": "TSHIRT-BLUE-M",
  "onHand": 40, "reserved": 5, "available": 35, "version": 7,
  "createdAt": "2026-11-24T10:20:30.456+00:00", "updatedAt": "2026-11-24T10:25:00.000+00:00"
}
```

- `available` is `onHand − reserved`. It is the current availability for checkout.
- `version` increases by one per movement.

**Endpoints:**

- `GET .../items/{itemId}`: one item, or `404 inventory-item-not-found`.
- `GET .../items?productId=&variantId=&sku=&page=&pageSize=`: the tenant's items in creation order.
  - The filters are exact.
  - `page` starts at 1. `pageSize` is 20 by default and at most 100.
  - The answer is `{ "items": [...], "page", "pageSize", "totalCount" }`.
  - Invalid values answer `400`.

## 5. Adjust stock

`POST .../items/{itemId}/adjustments` with an `Idempotency-Key` header:

```json
{ "quantityChange": -3, "reason": "Damage" }
```

**A signed, non-zero change, never an absolute balance** (ADR-026 section 4). `reason` is one of these exact names:

| Reason | Allowed change |
|---|---|
| `Receipt`, `Return` | positive |
| `Damage`, `Loss` | negative |
| `Correction`, `CycleCount` | either sign |

The item is locked. The balance change, its `Adjustment` movement (with the actor, the before and after values and the correlation ID) and its `StockLevelChanged` event commit in one transaction. Catalog is not asked.

**Answers:**

| Status | When |
|---|---|
| `201` | Applied. The body is `{ "movement": <movement>, "item": <item> }`. |
| `200` | The same key and body again: the original movement, and the item in its current state |
| `400 validation-failed` | A zero or out-of-range change, an unknown reason (names are case-sensitive), a sign the reason does not allow, or a missing key |
| `404 inventory-item-not-found` | No such item in the tenant |
| `409 insufficient-unreserved-stock` | On-hand stock would fall below the reserved quantity, or below zero |
| `409 stock-limit-exceeded` | On-hand stock would exceed 1,000,000,000 |
| `422 idempotency-key-reused` | The key was used for another change, reason or item |

**Idempotency scope:** (tenant, user subject, key). Concurrent retries of one key apply the change once.

## 6. Read the movement history

`GET .../items/{itemId}/movements?page=&pageSize=` returns the item's movements newest first, in descending `itemVersion`:

```json
{
  "id": "<guid>", "inventoryItemId": "<guid>", "itemVersion": 3, "type": "Adjustment", "reason": "Damage",
  "onHandDelta": -3, "reservedDelta": 0, "onHandBefore": 25, "reservedBefore": 0, "onHandAfter": 22, "reservedAfter": 0,
  "availableAfter": 22, "reservationId": null, "actorKind": "User", "actorId": "<subject>",
  "correlationId": "<id>", "occurredAt": "2026-11-24T10:22:00+00:00"
}
```

- **Types:** `InitialStock`, `Adjustment`, `Reservation`, `ReservationRelease`, `ReservationExpiry`, `ReservationCommit`.
- **Actors:** `User` (the subject), `Service` (the reservation's client ID), or `System` (`reservation-expiry`).
- **The ledger is append-only.** Replaying it in version order reproduces the balance.

## 7. Internal reservations

These endpoints are for checkout orchestration by a future Order service (ADR-026 sections 6 to 10). The Gateway never routes `/internal/**`.

**Callers:**

- a service token (`ec_ptype=service`, `sub == client_id`, `svc-` prefix)
- the audience `inventory-api`; another audience answers `401`
- the scope `svc.inventory.reservations`
- a client in `Inventory:Reservations:AllowedClientIds`, which is empty by default and accepts only `svc-` entries

Any other caller, a merchant user among them, answers `403`. **A reservation is visible only to the client that created it.** Another allowed client, or another tenant in the route, answers `404 reservation-not-found`. The client is trusted for the tenants it names: there is no membership check, since the allow-list is the authorization (as for the provisioning API of Tenant & Merchant).

**The reservation representation:**

```json
{
  "id": "<guid>", "tenantId": "<guid>", "status": "Active",
  "lines": [{ "inventoryItemId": "<guid>", "productId": "<guid>", "variantId": "<guid>", "quantity": 2 }],
  "reference": "order-42", "createdAt": "...", "expiresAt": "...", "completedAt": null, "version": 1
}
```

`status` is `Active`, `Committed`, `Released` or `Expired`. Terminal states are final, and `version` becomes 2 at the terminal transition.

### 7.1 Create a reservation

`POST /internal/v1/inventory/tenants/{tenantId}/reservations` with an `Idempotency-Key` header:

```json
{ "lines": [{ "variantId": "<guid>", "quantity": 2 }], "reference": "order-42", "expiresInSeconds": 900 }
```

**The request:**

- `lines`: 1 to 50 lines of distinct variants, each with a quantity of 1 to 1,000,000.
- `reference`: optional, 1 to 64 of `A-Z a-z 0-9 - _ . :`, never personal data.
- `expiresInSeconds`: optional, between 60 and `MaxLifetime` (1 hour by default). Without it, `DefaultLifetime` applies (15 minutes). The expiry is computed from the database clock.

**All or nothing.** Every line is checked against the tenant's items under their locks, taken in item ID order. Only if all fit are they reserved, together with the movements, the reservation, `ReservationCreated` and one `StockLevelChanged` per line, in one transaction.

| Status | When |
|---|---|
| `201` | Created. `Location` points to the reservation. |
| `200` | The same key and request (the lines in any order) again: the original reservation, **in its current state** |
| `400 validation-failed` | Invalid lines, reference or lifetime, or a missing `Idempotency-Key`. A fractional quantity or an unknown member is `400` too. |
| `409 insufficient-stock` | At least one line lacks available stock. The `variantIds` extension lists every short variant. Nothing is reserved. |
| `422 inventory-item-not-found` | A line names a variant without an inventory item in the tenant (`variantIds` lists them) |
| `422 idempotency-key-reused` | The key was used for a different request |

**Idempotency scope:** (client, tenant, key). A retry that arrives during the original waits for it and replays it. A failed attempt stores nothing.

### 7.2 Read a reservation

`GET .../reservations/{reservationId}` answers `200` with the reservation, or `404 reservation-not-found`.

### 7.3 Commit and release

`POST .../reservations/{reservationId}/commit` and `POST .../reservations/{reservationId}/release` take no body and no key: they are idempotent state transitions.

| Request | `Active` | `Committed` | `Released` | `Expired` | `Active` but past its expiry |
|---|---|---|---|---|---|
| commit | `200`: the units leave on-hand stock | `200`, unchanged | `409 reservation-released` | `409 reservation-expired` | expires now, then `409 reservation-expired` |
| release | `200`: the units become available | `409 reservation-committed` | `200`, unchanged | `200`, unchanged (status `Expired`) | expires now, then `200` (status `Expired`) |

Commit, release and the expiry worker lock the reservation, then its items, and re-read the state under the lock. A reservation is therefore transitioned exactly once whatever the concurrency, and it emits exactly one terminal event.

## 8. Health

See [INVENTORY_OPERATIONS.md section 3](../operations/INVENTORY_OPERATIONS.md#3-health).

## 9. Errors

Every error is RFC 9457 problem details. It has `type` `https://errors.enterprisecommerce.example/inventory/<code>`, `title`, `status`, `instance` (`<METHOD> <path>`) and `correlationId`, and it never carries exception details.

| Status | Codes |
|---|---|
| `400` | `validation-failed` (with `errors`); a body that cannot be bound uses the generic RFC 9110 type |
| `401` | No valid token (no body) |
| `403` | `permission-denied`, or a caller the policy refuses (no body) |
| `404` | `tenant-not-found`, `inventory-item-not-found`, `reservation-not-found` |
| `409` | `inventory-item-exists`, `variant-not-eligible`, `insufficient-unreserved-stock`, `stock-limit-exceeded`, `insufficient-stock` (with `variantIds`), `reservation-committed`, `reservation-released`, `reservation-expired`, `concurrency-conflict` |
| `413` | A body above 256 KiB |
| `422` | `variant-not-found`, `inventory-item-not-found` (with `variantIds`), `idempotency-key-reused` |
| `503` | `membership-unavailable`, `catalog-unavailable`, or the generic RFC 9110 type when SQL Server cannot complete the operation; all with `Retry-After: 5` |

## 10. Gateway

- **Development:** the Gateway routes `/api/v1/inventory/**` on `localhost:7000` to the service (port 7104).
- **Internal paths are never routed:** every `/internal/**` path answers `404` at the Gateway, including encoded and traversal forms.
- **No other service and no BFF calls the merchant API yet.** Order will call the internal API directly, not through the Gateway.
