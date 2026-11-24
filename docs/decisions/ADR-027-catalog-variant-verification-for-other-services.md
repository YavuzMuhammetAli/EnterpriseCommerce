# ADR-027: Catalog Variant Verification for Other Services

- **Status:** Accepted
- **Date:** 2026-11-24
- **Phase:** 5 (Inventory)
- **Related:**
  - [ADR-014](ADR-014-logical-tenant-isolation.md) (a tenant ID is not proof)
  - [ADR-017](ADR-017-token-session-and-protocol-strategy.md) (service tokens, per-service audiences)
  - [ADR-024](ADR-024-catalog-service-design.md) (sections 3, 5 and 13: variants, lifecycle, no events)
  - [ADR-025](ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md) (the verification pattern this ADR mirrors)
  - [ADR-026](ADR-026-inventory-service-design.md) (section 11: when Inventory verifies)
- **Resolves:** for Inventory, the Phase 4 deferred capability "Internal product reads for Cart and Order" (PHASE_4_CATALOG.md section 15), as one narrow verdict. Cart and Order will need their own reads, decided in their phases.
- **Design and evidence:** [PHASE_5_INVENTORY.md](../architecture/PHASE_5_INVENTORY.md).

## Context

Inventory must not trust a product ID, a variant ID, a SKU or a tenant ID supplied by a client (ADR-014 rule 4). Before it tracks stock for a variant, it must establish four things:

- the product exists
- it belongs to the tenant
- the variant belongs to the product
- the product's lifecycle allows tracking

**No Catalog endpoint answers this for another service:**

- The public reads return only `Published` products and active variants.
- The merchant API requires a user token. Inventory acting for the user would need token exchange, which is out of MVP scope (PHASE_2_2 section 5.7).
- The internal API serves only the category taxonomy.

**Catalog publishes no events** (ADR-024 section 13). Its standalone MongoDB has no transactional outbox. So a local projection is not available, and an event integration must not be invented.

## Decision

### 1. A narrow internal verification endpoint in Catalog

`POST /internal/v1/catalog/tenants/{tenantId}/variant-verifications`, with the body:

```json
{ "productId": "<guid>", "variantId": "<guid>" }
```

- **Unknown fields** answer `400`, and so does an empty or missing GUID.
- **The answer** is always `200` with `Cache-Control: no-store`:
  - a found variant: `{ "found": true, "productStatus": "Draft" | "Published" | "Archived", "variantActive": true | false, "sku": "<sku>" }`
  - otherwise: `{ "found": false, "productStatus": null, "variantActive": null, "sku": null }`
- **`found`** means a product with that ID **in that tenant** has a variant with that ID. A missing product, a product of another tenant and a missing variant all give the same negative verdict, so the endpoint reveals nothing about other tenants.
- **`productStatus`** is an open set; a consumer must fail closed on an unknown value. The answer contains nothing else: no price, no content, no audit data.
- **Caller requirements:**
  - a service token (`ec_ptype=service`, `sub == client_id`, `svc-` prefix)
  - the audience `catalog-api`
  - the scope `svc.catalog.variant-verification`
  - a client listed in `Catalog:VariantVerification:AllowedClientIds`

  The list is **empty by default**, and startup refuses an entry without the `svc-` prefix. The policy and the list are separate from the taxonomy's (`svc.catalog.taxonomy`). A client allowed for one is not allowed for the other.
- **Routing:** like every internal path, the Gateway never routes it.

### 2. How Inventory uses the verdict

- **When:** only when an inventory item is created (ADR-026 section 11). It is never called for reads, adjustments or reservations.
- **The service token.** Inventory calls as `svc-inventory`, using the client credentials grant with `private_key_jwt`, like ADR-025. The token is cached until 60 seconds before it expires, and invalidated after a `401` with one retry. The verdict is never cached.
- **Eligibility.** An item may be created when `found` is true and `productStatus` is `Draft` or `Published`:
  - `Archived` answers `409 variant-not-eligible`.
  - **An unknown `productStatus` also answers `409 variant-not-eligible`**, failing closed.
  - `found: false` answers `422 variant-not-found`.
  - `variantActive` does not affect eligibility.
- **Fail closed.** Any of the following answers `503 catalog-unavailable` with `Retry-After`, and nothing is written:
  - no Catalog client is configured, which is allowed only in Development
  - a timeout (5 seconds by default)
  - a transport error
  - a non-`200` status
  - an unknown JSON shape
  - a token failure
- **Correlation.** `X-Correlation-ID` is propagated.

## Consequences

- **Positive:** Inventory can only track variants that Catalog confirms for the tenant. A forged or cross-tenant product reference is refused.
- **Positive:** the contract is minimal, and no product content or pricing leaves Catalog through it.
- **Negative:** item creation depends on Catalog's availability. Every other inventory operation does not.
- **Negative:** the verdict is a point-in-time fact. Later archival or SKU changes are not seen by Inventory (ADR-026 section 11).

## Alternatives considered

- **A general internal product read:** broader than Inventory needs, and Cart and Order will need different shapes. Rejected for now.
- **`GET` with the IDs in the path:** acceptable for non-personal IDs, but the `POST` verification shape of ADR-025 keeps one pattern, and keeps the request out of URL-based caches and logs. Not adopted.
- **Answering `404` for an unknown variant:** indistinguishable from a routing error, which must fail closed as `503`. Rejected (as in ADR-025).
- **Reusing the taxonomy allow-list:** it would let a taxonomy writer read tenant products and vice versa. Rejected.
- **Catalog events with a projection in Inventory:** that needs a MongoDB replica set and a MongoDB outbox design (ADR-024 section 13). Out of Phase 5. Rejected for now.
- **Verifying on every reservation:** this couples checkout to Catalog. Rejected (ADR-026 section 11).
