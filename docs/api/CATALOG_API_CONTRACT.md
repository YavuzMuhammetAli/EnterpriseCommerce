# Catalog API Contract

- **Status:** Accepted and implemented in Phase 4 ([PHASE_4_CATALOG.md](../architecture/PHASE_4_CATALOG.md)).
- **Decisions:**
  - [ADR-024](../decisions/ADR-024-catalog-service-design.md): the Catalog design
  - [ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md): merchant authorization
- **Conventions:** the cross-service conventions of [IDENTITY_API_CONTRACT.md section 1](IDENTITY_API_CONTRACT.md#1-conventions-baseline) apply:
  - `/api/v1`, lowercase kebab-case paths, camelCase JSON
  - `X-Correlation-ID` and `Idempotency-Key`
  - RFC 9457 problem details

  Pagination follows [README.md](README.md#conventions). Catalog adds entity tags for optimistic concurrency ([section 1](#1-summary)).

## 1. Summary

| Endpoint | Caller | Section |
|---|---|---|
| `GET /api/v1/catalog/products` | Anyone (anonymous) | [2.1](#21-list-published-products) |
| `GET /api/v1/catalog/products/{productId}` | Anyone | [2.2](#22-read-a-published-product) |
| `GET /api/v1/catalog/products/lookup?tenantId=&slug=` | Anyone | [2.3](#23-look-up-a-published-product-by-slug) |
| `GET /api/v1/catalog/categories`, `GET /api/v1/catalog/categories/{categoryId}` | Anyone | [3](#3-public-categories) |
| `POST`, `GET /api/v1/catalog/tenants/{tenantId}/products` | Merchant member (`EditProducts`, `ViewCatalog`) | [4.1](#41-create-a-product), [4.2](#42-list-the-tenants-products) |
| `GET`, `PUT /api/v1/catalog/tenants/{tenantId}/products/{productId}` | Merchant member (`ViewCatalog`, `EditProducts`) | [4.3](#43-read-a-product), [4.4](#44-replace-a-products-content) |
| `PUT .../products/{productId}/media` | Merchant member (`EditProducts`) | [4.5](#45-replace-a-products-media) |
| `POST .../products/{productId}/publish`, `/unpublish`, `/archive`, `/restore` | `MerchantOwner`, `MerchantAdmin` (`PublishProducts`) | [4.6](#46-lifecycle-commands) |
| `POST`, `GET`, `GET {brandId}`, `PUT {brandId}`, `POST {brandId}/retire`, `/reactivate` under `/api/v1/catalog/tenants/{tenantId}/brands` | Reads: any member; changes: `MerchantOwner`, `MerchantAdmin` | [5](#5-brands) |
| `/internal/v1/catalog/categories` (create, list, read, replace, retire, reactivate) | Allow-listed service client (scope `svc.catalog.taxonomy`) | [6](#6-internal-category-taxonomy) |
| `POST /internal/v1/catalog/tenants/{tenantId}/variant-verifications` | Allow-listed service client (scope `svc.catalog.variant-verification`) | [6a](#6a-internal-variant-verification) |
| `GET /health`, `/health/live`, `/health/ready` | Anonymous | [8](#8-health) |

**Rules for every endpoint:**

- **Request bodies** reject members the contract does not define, at every nesting level, with `400`. No tenant, status, version or stock field can be posted.
- **Body size.** Bodies are limited to 256 KiB. A larger one answers `413`.
- **Entity tags.**
  - Every response that carries one resource has `ETag: "<version>"`.
  - A full replacement (`PUT` of a product, media list, brand or category) requires `If-Match: "<version>"`.
    - No `If-Match` answers `428 precondition-required`.
    - A malformed, weak or wildcard tag, or a version that is not current, answers `412 precondition-failed`.
    - A concurrent change between read and write answers `409 concurrency-conflict`.
  - State commands need no tag. Repeating one whose target state is already in effect answers `200` and changes nothing.
- **Pagination.** `page` (1-based) and `pageSize` (default 20, at most 100); public product listings allow at most 100 pages. Responses have the shape `{ "items": [...], "page": 1, "pageSize": 20, "totalCount": 42 }`.

## 2. Public discovery

Anonymous. Any `Authorization` header is ignored. Only `Published` products are ever returned.

A **public product** contains:

```json
{
  "id": "…", "tenantId": "…", "slug": "blue-shirt", "name": "Blue shirt",
  "shortDescription": "…", "description": "…",
  "brand": { "id": "…", "slug": "acme", "name": "Acme" },
  "categories": [ { "id": "…", "slug": "shirts", "name": "Shirts" } ],
  "attributes": [ { "key": "material", "value": "Cotton" } ],
  "variants": [ { "id": "…", "sku": "BS-M", "options": [ { "key": "size", "value": "M" } ], "price": { "amount": 19.99, "currency": "EUR" }, "isActive": true } ],
  "media": [ { "id": "…", "url": "https://cdn.example.com/a.jpg", "altText": "Front", "contentType": "image/jpeg" } ],
  "publishedAt": "2026-11-23T10:00:00+00:00"
}
```

**What is included:**

- **Variants:** only active ones.
- **Categories:** only active ones.
- **Brand:** `null` unless an active brand of the same tenant.
- **`tenantId`:** identifies the selling merchant.

**What is never included:** status, version, timestamps other than `publishedAt`, audit subjects, idempotency data and stock. Catalog has no stock data; Inventory owns it.

### 2.1 List published products

`GET /api/v1/catalog/products`

| Parameter | Rule |
|---|---|
| `q` | 2 to 100 characters, at most 10 words. `-` and `"` are ignored. Whole-word matching on the name, short description and description, words combined with OR, no stemming. |
| `categoryId` | a GUID; also matches products of its descendant categories |
| `brandId` | a GUID |
| `tenantId` | a GUID (one merchant's products) |
| `attr` | `key:value`, repeatable up to 5 times. Each pair must match within one attribute. Values compare without regard to case. |
| `sort` | `newest` (default) or `oldest`, by publication time, then ID |
| `page`, `pageSize` | `page` at most 100, `pageSize` at most 100 |

Any invalid value answers `400 validation-failed`. No other parameter, field or expression influences the query.

### 2.2 Read a published product

`GET /api/v1/catalog/products/{productId}`

- `200`: a public product.
- `404 product-not-found`: the product is unknown, a draft or archived. These cases are indistinguishable.

### 2.3 Look up a published product by slug

`GET /api/v1/catalog/products/lookup?tenantId={tenantId}&slug={slug}`. Slugs are unique per tenant, so both are required (`400` otherwise).

- `200`: a public product.
- `404 product-not-found`.

## 3. Public categories

**Reads:**

- `GET /api/v1/catalog/categories` (paged, oldest first, so every parent precedes its children)
- `GET /api/v1/catalog/categories/{categoryId}`

Both are anonymous and return active categories only:

```json
{ "id": "…", "slug": "shirts", "name": "Shirts", "description": null, "parentId": "…", "ancestorIds": ["…"], "depth": 2 }
```

A retired or unknown category answers `404 category-not-found`.

## 4. Merchant products

**Callers.** Merchant-portal users through the merchant BFF: a user token with audience `ec-merchant` (section 7).

**Authorization, on every request:**

- The tenant in the path is checked against Tenant & Merchant's verdict for the caller.
- A caller who is not an active member of the tenant gets `404 tenant-not-found`, whether or not the tenant exists.
- A role that lacks the permission gets `403 permission-denied`.
- Without a verdict, the request answers `503 membership-unavailable` with `Retry-After`.

**Scoping.** Every read and write is scoped to the path tenant, so a product of another tenant answers `404 product-not-found`.

**A merchant product** has every field of a public product (with `brandId` and `categoryIds` instead of the resolved objects, and every variant), plus:
- `status` (`Draft`, `Published` or `Archived`)
- `createdAt`, `updatedAt`, `publishedAt`, `archivedAt`
- `version`

### 4.1 Create a product

`POST /api/v1/catalog/tenants/{tenantId}/products`. Requires `Idempotency-Key` (1 to 100 characters of `A-Za-z0-9._:-`).

```json
{
  "slug": "blue-shirt",
  "name": "Blue shirt",
  "shortDescription": "Optional, at most 500 characters on one line.",
  "description": "Optional, at most 10,000 characters of plain text.",
  "brandId": null,
  "categoryIds": ["…"],
  "attributes": [ { "key": "material", "value": "Cotton" } ],
  "variants": [ { "sku": "BS-M", "options": [ { "key": "size", "value": "M" } ], "price": { "amount": 19.99, "currency": "EUR" }, "isActive": true } ]
}
```

**Validation rules:**

| Field | Rule |
|---|---|
| `slug` | 3 to 100 characters of `a-z` and `0-9`, in groups separated by single hyphens. Unique per tenant. |
| `name` | 1 to 200 characters on one line |
| `categoryIds` | at most 5, distinct, existing and active |
| `brandId` | an active brand of the same tenant. A brand of another tenant is reported like a missing one. |
| `attributes` | at most 50, with unique keys. A key is 1 to 40 characters of `a-z`, `0-9` and `-`, not starting with `-`. A value is 1 to 100 characters on one line. |
| `variants` | at most 100 |
| `variants[].sku` | 1 to 64 characters of `A-Za-z0-9._-`, unique per tenant across all its products, compared without regard to case |
| `variants[].options` | at most 10, using the attribute rules. The option set is distinct among the product's variants. |
| `variants[].price` | `amount` greater than 0 and at most 1,000,000,000, with at most 2 decimals. `currency` one of `EUR`, `USD`, `GBP`, `TRY`. One currency per product. |
| `variants[].isActive` | defaults to `true` |

**Answers:**

| Status | When |
|---|---|
| `201` | Created: a `Draft` product with `Location` and `ETag` |
| `200` | The same key and the same body as an earlier creation: that product (a replay) |
| `422 idempotency-key-reused` | The same key with a different body |
| `400 validation-failed` | Field errors in `errors`, for example `variants[0].sku`. A missing or malformed key is reported under `Idempotency-Key`. |
| `409 slug-taken` | The slug is used by another product of the tenant |
| `409 sku-taken` | A SKU is used by another product of the tenant |

**Idempotency.** Keys are scoped per tenant and user, and kept for the product's lifetime. Concurrent retries with the same key create one product, and the others are answered as replays.

### 4.2 List the tenant's products

`GET /api/v1/catalog/tenants/{tenantId}/products` returns products in every status.

| Parameter | Rule |
|---|---|
| `status` | `Draft`, `Published` or `Archived` (exact names) |
| `q` | the text query of section 2.1 |
| `sku` | an exact SKU, compared without regard to case |
| `sort` | `newest` (default) or `oldest`, by update time, then ID |
| `page`, `pageSize` | the platform convention |

### 4.3 Read a product

`GET /api/v1/catalog/tenants/{tenantId}/products/{productId}`

- `200` with `ETag`
- `404 product-not-found`

### 4.4 Replace a product's content

`PUT /api/v1/catalog/tenants/{tenantId}/products/{productId}`, with `If-Match`.

**The body** is the complete content, with the same shape as section 4.1. Variants are replaced as a whole list:
- a variant with the `id` of an existing variant keeps that ID
- a variant without an `id` is new
- an unknown `id` answers `400`

**Answers:**

| Status | When |
|---|---|
| `200` | The updated product, with its new `ETag` |
| `409 product-archived` | The product is archived |
| `409 publication-requirements-not-met` | The product is published and the change would break a publication requirement (section 4.6). Nothing changes. |
| `400`, `409 slug-taken`, `409 sku-taken` | As in section 4.1 |
| `428`, `412`, `409 concurrency-conflict` | Section 1 |

**References.** A category or brand the product already has may have been retired since. It may stay, but blocks publication. Newly assigned references must be active.

### 4.5 Replace a product's media

`PUT /api/v1/catalog/tenants/{tenantId}/products/{productId}/media`, with `If-Match`.

```json
{ "media": [ { "url": "https://cdn.example.com/a.jpg", "altText": "Front", "contentType": "image/jpeg" } ] }
```

**The list.** At most 20 references, in display order. An `id` of an existing reference keeps it.

**Field rules:**

| Field | Rule |
|---|---|
| `url` | An absolute `https` URL of at most 2,048 characters. No user information and no fragment. The host is a DNS name with a dot: not an IP literal, and not `localhost` or a `.localhost`, `.local` or `.internal` host. |
| `altText` | At most 250 characters on one line |
| `contentType` | `image/jpeg`, `image/png`, `image/webp` or `image/avif` |

**What Catalog does with the URL.** It never fetches the URL and stores no binary media. Images must be hosted elsewhere; there is no upload endpoint.

An archived product answers `409 product-archived`.

### 4.6 Lifecycle commands

`POST /api/v1/catalog/tenants/{tenantId}/products/{productId}/{command}`, with no body.

| Command | From | To |
|---|---|---|
| `publish` | `Draft` | `Published` |
| `unpublish` | `Published` | `Draft` |
| `archive` | `Draft`, `Published` | `Archived` |
| `restore` | `Archived` | `Draft` |

**Answers:**

- `200`: the product. This includes the case where the target state is already in effect.
- `409 invalid-transition`: any other transition.
- `409 publication-requirements-not-met`: the product cannot be published. `requirements` lists the stable codes:
  - `category-required`
  - `category-inactive`
  - `brand-inactive`
  - `active-variant-required`

## 5. Brands

**The brand resource.** Brands are tenant-scoped:

```json
{ "id": "…", "tenantId": "…", "slug": "acme", "name": "Acme", "description": null, "status": "Active", "createdAt": "…", "updatedAt": "…", "version": 1 }
```

**Endpoints:**

| Request | Body | Answers |
|---|---|---|
| `POST /api/v1/catalog/tenants/{tenantId}/brands` | `{ "slug", "name", "description" }` | `201`, `400`, `409 slug-taken` (slugs are unique per tenant) |
| `GET .../brands` (paged, oldest first), `GET .../brands/{brandId}` | | `200`, `404 brand-not-found` |
| `PUT .../brands/{brandId}` | the same body, with `If-Match` | `200`, `400`, `409 slug-taken`, `428`, `412`, `409 concurrency-conflict` |
| `POST .../brands/{brandId}/retire`, `/reactivate` | | `200` |

**Rules:**

- **No deletion.** Brands are never deleted.
- **Retired brands.** A retired brand cannot be newly assigned, blocks publication, and disappears from public product views.
- **Permissions.** Reads need `ViewCatalog`. Changes need `ManageBrands` (`MerchantOwner`, `MerchantAdmin`).

## 6. Internal category taxonomy

**Reachability.** The taxonomy API is on the internal network only; the Gateway never routes `/internal/**`.

**Accepted callers.** A service token is accepted only if all of these hold:
- `ec_ptype=service`
- the audience `catalog-api`
- the scope `svc.catalog.taxonomy`
- `sub` equal to `client_id`, with the `svc-` prefix
- the client is listed in `Catalog:Taxonomy:AllowedClientIds`

The list is empty by default, and startup refuses an entry without the `svc-` prefix. The intended caller is Admin (`svc-admin`), which does not exist yet.

**The category resource:**

```json
{ "id": "…", "slug": "shirts", "name": "Shirts", "description": null, "parentId": "…", "ancestorIds": ["…"], "depth": 2, "status": "Active", "createdAt": "…", "updatedAt": "…", "version": 1 }
```

**Endpoints:**

| Request | Body | Answers |
|---|---|---|
| `POST /internal/v1/catalog/categories` | `{ "slug", "name", "description", "parentId" }` | `201`; `400`; `409 slug-taken` (slugs are unique across the platform); `409 parent-unavailable` (the parent is unknown or retired); `409 category-too-deep` (more than 5 levels) |
| `GET /internal/v1/catalog/categories` (paged, every status), `GET .../{categoryId}` | | `200`, `404 category-not-found` |
| `PUT .../{categoryId}` | `{ "slug", "name", "description" }`, with `If-Match` | `200`; a `parentId` member answers `400` (the parent never changes) |
| `POST .../{categoryId}/retire` | | `200`; `409 category-has-active-children` |
| `POST .../{categoryId}/reactivate` | | `200`; `409 parent-unavailable` (the parent is retired) |

**Identity configuration for a caller** (example values):

```
Identity__Clients__N__ClientId=svc-admin
Identity__Clients__N__Kind=Service
Identity__Clients__N__ServiceScopes__0__Scope=svc.catalog.taxonomy
Identity__Clients__N__ServiceScopes__0__Audience=catalog-api
Catalog__Taxonomy__AllowedClientIds__0=svc-admin
```

## 6a. Internal variant verification

Added in Phase 5 for Inventory ([ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md)). It answers an allow-listed service client whether a variant belongs to a product of a tenant, with the product's status. It never returns product content or prices.

`POST /internal/v1/catalog/tenants/{tenantId}/variant-verifications`

```json
{ "productId": "<guid>", "variantId": "<guid>" }
```

**Answers:**

- `200` with `Cache-Control: no-store`. A found variant answers:

  ```json
  { "found": true, "productStatus": "Draft", "variantActive": true, "sku": "TSHIRT-BLUE-M" }
  ```

  Anything else answers:

  ```json
  { "found": false, "productStatus": null, "variantActive": null, "sku": null }
  ```

- `found` is true only when a product with that ID in that tenant has a variant with that ID. A missing product, another tenant's product and a missing variant all give the same negative verdict.
- `productStatus` is an open set (`Draft`, `Published`, `Archived` today); a caller must fail closed on an unknown value.
- A missing or empty ID, or an unknown body member, answers `400`.

**Caller requirements:**

- a service token (`ec_ptype=service`, `sub == client_id`, `svc-` prefix)
- the audience `catalog-api`; another audience answers `401`
- the scope `svc.catalog.variant-verification`
- a client in `Catalog:VariantVerification:AllowedClientIds`, which is empty by default and accepts only `svc-` entries

Any other caller answers `403`. The allow-list is separate from the taxonomy's. Inventory's client is `svc-inventory`:

```
Identity__Clients__N__ClientId=svc-inventory
Identity__Clients__N__Kind=Service
Identity__Clients__N__ServiceScopes__0__Scope=svc.catalog.variant-verification
Identity__Clients__N__ServiceScopes__0__Audience=catalog-api
Catalog__VariantVerification__AllowedClientIds__0=svc-inventory
```

## 7. Authentication and authorization

**Token validation.** Tokens are validated locally (ADR-017 section 4, through `EnterpriseCommerce.BuildingBlocks.Authentication`):
- the signature against Identity's JWKS, fetched from `Catalog:Authentication:MetadataAddress` (by default the issuer's metadata)
- the exact issuer `Catalog:Authentication:Issuer`
- `RS256` only, and `typ: at+jwt`
- 30 seconds of clock skew
- an audience of `catalog-api` (`ServiceAudience`) or `ec-merchant` (`UserAudiences`)

**Answers:**

| Result | When |
|---|---|
| `401` | A protected endpoint without a token valid for this service. This covers a token with the storefront audience. |
| `403` | A valid token of the wrong kind of caller: a service token on a merchant endpoint, a user token on an internal endpoint, a missing scope, a client that is not allow-listed, or a role without the permission |

**Roles and tenants.**
- Tokens carry no roles and no tenant. A forged `role`, `roles`, `tenant_id` or `tenant_context` claim is ignored.
- Merchant roles come from Tenant & Merchant's verdict on every request ([TENANT_MERCHANT_API_CONTRACT.md section 3.3](TENANT_MERCHANT_API_CONTRACT.md#33-verify-a-membership-internal)). Verdicts are never cached.

**Permissions** (ADR-025 section 2):

| Permission | `MerchantOwner` | `MerchantAdmin` | `MerchantStaff` |
|---|---|---|---|
| `ViewCatalog` | yes | yes | yes |
| `EditProducts` | yes | yes | yes |
| `PublishProducts` | yes | yes | no |
| `ManageBrands` | yes | yes | no |

An unknown role grants nothing.

## 8. Health

| Endpoint | Meaning |
|---|---|
| `/health`, `/health/live` | Process liveness only, never dependent on MongoDB |
| `/health/ready` | `Healthy` when MongoDB answers a ping and every index exists; otherwise `503` |

Responses carry the status word only.

## 9. Errors

**Format.** Errors are RFC 9457 problem details (`application/problem+json`), with:
- `type`
- `title`
- `status`
- `instance` (`"{METHOD} {path}"`)
- `traceId`
- `correlationId`
- `errors`, for validation failures

**Types.** The `type` is `https://errors.enterprisecommerce.example/catalog/{code}` (placeholder scheme):

| Status | Code |
|---|---|
| 400 | `validation-failed` (a body that cannot be read, such as malformed JSON or an unknown member, answers 400 with the generic RFC 9110 type) |
| 403 | `permission-denied` |
| 404 | `tenant-not-found`, `product-not-found`, `brand-not-found`, `category-not-found` |
| 409 | `slug-taken`, `sku-taken`, `concurrency-conflict`, `product-archived`, `publication-requirements-not-met` (with `requirements`), `invalid-transition`, `parent-unavailable`, `category-too-deep`, `category-has-active-children` |
| 412 | `precondition-failed` |
| 413 | the generic RFC 9110 type: the body exceeds 256 KiB |
| 422 | `idempotency-key-reused` |
| 428 | `precondition-required` |
| 503 | `membership-unavailable` (no verdict from Tenant & Merchant), or the generic RFC 9110 type when MongoDB cannot complete the operation. Both carry `Retry-After: 5`. |

**What errors never contain:** exception messages, stack traces and store details.

## 10. Gateway and BFF

- **Gateway.** The Development Gateway routes `/api/v1/catalog/{**catch-all}` on `localhost:7000` to Catalog (`catalog-api` route, `https://localhost:7103/`). It never routes `/internal/**`.
- **Merchant BFF.** It reaches the merchant API without any change: its `/api/**` forwarding attaches the session's `ec-merchant` access token.
- **Storefront BFF.** It forwards only for signed-in sessions. Anonymous browsing through it is deferred; it needs a BFF design. Public discovery is served on the Gateway's API host.
