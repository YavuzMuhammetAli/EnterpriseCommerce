# Phase 4: Catalog Service

The Catalog microservice (`EnterpriseCommerce.Catalog`). It owns product content, the platform category taxonomy, tenant brands, variants with their list prices, product attributes, media references, the publication lifecycle and catalog discovery.

- **Decisions:**
  - [ADR-024](../decisions/ADR-024-catalog-service-design.md): the Catalog design
  - [ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md): merchant authorization through Tenant & Merchant, and shared token validation
- **Contract:** [CATALOG_API_CONTRACT.md](../api/CATALOG_API_CONTRACT.md).
- **Operations:** [CATALOG_OPERATIONS.md](../operations/CATALOG_OPERATIONS.md).
- **Status:** in progress. The design is accepted (2026-11-23). The implementation follows the milestones in [section 10](#10-milestones-and-progress).

## 1. Inputs and scope

**Baseline:**

- `main` at `3e713df`, equal to `origin/main`, with a clean working tree.
- Phase 3 S4 is complete, and the project owner confirmed that its CI run succeeded.

**Read before the design:**

- PROJECT_DECISIONS, SERVICE_CATALOG and SOLUTION_STRUCTURE
- ADR-004, ADR-005, ADR-009, ADR-011, ADR-012, ADR-014, ADR-017, ADR-020, ADR-022 and ADR-023
- the Phase 3 documents and the events documents
- the Tenant & Merchant implementation, the Gateway, the BFF, Compose, CI and the architecture tests

**Findings that shaped the design:**

| Finding | Consequence |
|---|---|
| The Compose MongoDB (`mongo:8.0.38`) is a standalone node, so there are no multi-document transactions | One product is one document, and every write is a single-document atomic write. No transactional outbox is possible, so there are no integration events in Phase 4 (ADR-024 section 13). |
| There are no membership events, and Phase 4 excludes consumers | No local membership projection. Tenant & Merchant gets a narrow internal verification endpoint (ADR-025). |
| Tenant & Merchant's member API refuses service tokens | The new endpoint is internal, with its own scope and allow-list. |
| The Gateway routes `/api/v1/tenants/**` to Tenant & Merchant | Every Catalog route lives under `/api/v1/catalog/**`. |
| The BFF forwards `/api/**` only with a session | Merchant calls reach Catalog through the merchant BFF unchanged. Anonymous storefront browsing through the storefront BFF needs a BFF design and is deferred. Public reads are served on the Gateway's API host. |
| ADR-017 section 4 asks for a token-validation BuildingBlock once a second service needs it | `EnterpriseCommerce.BuildingBlocks.Authentication` (ADR-025 section 3). |
| Compose runs infrastructure only. Services run with `dotnet run` and no Dockerfiles exist | Catalog follows that convention, with ports 5103 and 7103 and the existing `mongodb` profile. |

**Owner decisions (2026-11-23):**

- Categories are a platform taxonomy, written by allow-listed service clients.
- Brands are tenant-scoped.

**Out of scope:**

- inventory, cart, order, payment and fulfillment
- event consumers and inboxes
- an image upload pipeline
- a search engine
- approval workflows

## 2. Domain model

The Domain project has no dependencies. Business outcomes are results (`TryCreate` and outcome enums), never exceptions.

### Product (aggregate, collection `products`)

| Field | Rule |
|---|---|
| `Id` | GUID version 7, also the public ID |
| `TenantId` | Set at creation from the authorized route tenant; immutable, in no update contract |
| `Slug` | 3 to 100 characters, `^[a-z0-9]+(-[a-z0-9]+)*$`, unique per tenant, never normalized |
| `Name` | 1 to 200 characters after trimming, no control characters |
| `ShortDescription` | optional, at most 500 characters |
| `Description` | optional, at most 10,000 characters of plain text (line breaks and tabs allowed) |
| `BrandId` | optional; an active brand of the same tenant when assigned |
| `CategoryIds` | 0 to 5 distinct platform categories, active when assigned |
| `CategoryTreeIds` | the categories plus their ancestors (denormalized; ancestry is immutable) |
| `Attributes` | at most 50 entries of `key` (`^[a-z0-9][a-z0-9-]{0,39}$`, unique) and `value` (1 to 100 characters) |
| `Variants` | at most 100 (below) |
| `Media` | at most 20 references (below), in display order |
| `Status` | `Draft`, `Published` or `Archived` |
| `CreatedAt`, `UpdatedAt`, `PublishedAt`, `ArchivedAt` | UTC, millisecond precision |
| `CreatedBy`, `UpdatedBy` | Identity subject references (audit) |
| `Version` | optimistic concurrency; exposed as the entity tag |
| `Creation` | the creating subject, the `Idempotency-Key` and the request hash |

**Variant:**

| Field | Rule |
|---|---|
| `Id` | stable GUID |
| `Sku` | 1 to 64 characters of `A-Z a-z 0-9 . _ -`. It is compared case-insensitively and is unique per tenant across all of its products. |
| `Options` | at most 10 entries. Keys and values follow the attribute rules. The option set is distinct among the product's variants. |
| `Price` | `Money`: an amount greater than 0 and at most 1,000,000,000, with at most 2 decimals. The currency is one of `EUR`, `USD`, `GBP` or `TRY`. All variants of a product share one currency. |
| `IsActive` | whether the variant is sellable |

**Media reference:**

| Field | Rule |
|---|---|
| `Id` | GUID |
| `Url` | absolute `https`, at most 2,048 characters. No user information or fragment, no IP literal, and no `localhost` or `.localhost`, `.local` or `.internal` host. |
| `AltText` | optional, at most 250 characters |
| `ContentType` | `image/jpeg`, `image/png`, `image/webp` or `image/avif` |

No field holds stock, availability or reservation data.

### Category (aggregate, collection `categories`, platform-wide)

| Field | Rule |
|---|---|
| `Id`, `Slug` | the slug is unique across the platform (same slug rule as products) |
| `Name`, `Description` | the same rules as product name and short description |
| `ParentId`, `AncestorIds`, `Depth` | the parent is fixed at creation and must exist and be active. Depth is at most 5. |
| `Status` | `Active` or `Retired`. A category is retired only without active children, and reactivated only under an active parent. |
| `CreatedAt`, `UpdatedAt`, `Version` | |

### Brand (aggregate, collection `brands`, tenant-scoped)

| Field | Rule |
|---|---|
| `Id`, `TenantId` | the tenant is immutable |
| `Slug` | unique per tenant |
| `Name` | 1 to 100 characters |
| `Description` | optional, at most 500 characters |
| `Status` | `Active` or `Retired` |
| `CreatedAt`, `UpdatedAt`, `Version` | |

## 3. Product lifecycle

```
Draft --publish--> Published --unpublish--> Draft
Draft | Published --archive--> Archived --restore--> Draft
```

- **Repeating a command** whose target state is already in effect answers `200` and changes nothing. Any other transition answers `409 invalid-transition`.
- **Archived products** accept only `restore`. A content change answers `409 product-archived`.
- **Publication requirements:**
  - at least one category, with every referenced category active
  - the brand, if set, active
  - at least one active variant
- **Revalidation.** A change to a `Published` product must keep those requirements, otherwise it answers `409 publication-requirements-not-met`. The service never unpublishes silently.
- **Reference checks.** Catalog validates the referenced categories and brands when they are assigned and at every publish.

## 4. Persistence

**Storage:**

- One MongoDB database per service, `catalog` by default, holding three collections: `products`, `categories` and `brands`.
- **Mapping.** Explicit persistence documents in Infrastructure, mapped by hand. GUIDs use the standard representation, money is `Decimal128` and times are BSON dates.

**Consistency:**

- **Writes.** Every write is one single-document operation.
- **Optimistic concurrency.** A replacement filters on `_id`, the tenant (where it applies) and the expected `version`, and increments it.
- **Duplicate keys.** A duplicate key is translated by the name of the rejecting unique index. The driver does not expose the server's `keyPattern`, so the name is read from the server message and accepted only if it is one of the five known unique index names. The message itself contains stored values and is never logged or returned.
- **References.** Category and brand references are validated in the Application layer before the write. The residual race and its bounds are in ADR-024 section 11.

**Failures:**

- Connection, server-selection, timeout and server time-limit errors answer `503` with `Retry-After`, and nothing is reported as done.
- **Readiness.** The service starts without MongoDB. A background initializer creates the indexes and retries with backoff. Until it succeeds:
  - readiness is `Unhealthy`
  - every write answers `503`
- **Index conflicts.** An index option or key conflict is permanent: it is logged at error level, and readiness stays down.

## 5. Indexes

| Collection | Name | Keys | Options | Query it serves |
|---|---|---|---|---|
| products | `ux_products_tenant_slug` | `tenantId`, `slug` | unique | slug uniqueness; public lookup by tenant and slug |
| products | `ux_products_tenant_sku` | `tenantId`, `variants.skuKey` | unique, partial (`variants.skuKey` exists) | SKU uniqueness across products; merchant SKU filter |
| products | `ux_products_tenant_creation` | `tenantId`, `creation.subjectId`, `creation.idempotencyKey` | unique | idempotent creation |
| products | `ix_products_tenant_updated` | `tenantId`, `updatedAt` desc, `_id` desc | | merchant listing (status filter within the tenant) |
| products | `ix_products_public_published` | `status`, `publishedAt` desc, `_id` desc | | public listing |
| products | `ix_products_public_category` | `status`, `categoryTreeIds`, `publishedAt` desc, `_id` desc | | public category filter |
| products | `ix_products_public_brand` | `status`, `brandId`, `publishedAt` desc, `_id` desc | | public brand filter |
| products | `ix_products_public_tenant` | `status`, `tenantId`, `publishedAt` desc, `_id` desc | | public merchant storefront listing |
| products | `ix_products_attributes` | `attributes.key`, `attributes.valueKey`, `status` | | attribute filter |
| products | `tx_products_search` | text: `name` (10), `shortDescription` (5), `description` (1) | `default_language: none` | `q` |
| categories | `ux_categories_slug` | `slug` | unique | slug uniqueness |
| categories | `ix_categories_parent` | `parentId`, `status` | | active-children check on retire |
| categories | `ix_categories_listing` | `status`, `createdAt`, `_id` | | category listing (parents sort before children) |
| brands | `ux_brands_tenant_slug` | `tenantId`, `slug` | unique | slug uniqueness |
| brands | `ix_brands_tenant_created` | `tenantId`, `createdAt`, `_id` | | merchant brand listing |

The `oldest` sort reads the same indexes in reverse.

## 6. Authorization

- **Token validation** (ADR-017 section 4, through `BuildingBlocks.Authentication`):
  - exact issuer
  - RS256, `at+jwt`, 30 seconds of skew
  - the audiences `catalog-api` (service tokens) and `ec-merchant` (merchant users)
  - a role claim type no token carries
- **Fallback policy.** Every endpoint requires an authenticated caller unless it is explicitly public.
- **Public discovery** endpoints are anonymous. Any token they receive is ignored.
- **Merchant endpoints** (`merchant-user` policy) require an `ec-merchant` user token (`ec_ptype=user`, a `sub`, a non-`svc-` client), then the per-request verdict of ADR-025:

  | Situation | Answer |
  |---|---|
  | not an active member, or the tenant is unknown | `404 tenant-not-found` |
  | role lacks the permission | `403 permission-denied` |
  | no verdict | `503 membership-unavailable` |

  Resources of another tenant answer `404`, because every query and write includes the route tenant.
- **Taxonomy endpoints** (`taxonomy-service` policy) require:
  - a service token
  - the audience `catalog-api`
  - the scope `svc.catalog.taxonomy`
  - a client in `Catalog:Taxonomy:AllowedClientIds` (empty by default)
- **Denials** are logged as security events.

## 7. HTTP surface

The contract is [CATALOG_API_CONTRACT.md](../api/CATALOG_API_CONTRACT.md). In summary:

- **Public:**
  - `GET /api/v1/catalog/products`
  - `GET /api/v1/catalog/products/{productId}`
  - `GET /api/v1/catalog/products/lookup`
  - `GET /api/v1/catalog/categories`
  - `GET /api/v1/catalog/categories/{categoryId}`
- **Merchant products,** under `/api/v1/catalog/tenants/{tenantId}/products`:
  - create (`Idempotency-Key`), list, get, and replace (`If-Match`)
  - `publish`, `unpublish`, `archive` and `restore`
  - the media list (`If-Match`)
- **Merchant brands,** under `/api/v1/catalog/tenants/{tenantId}/brands`:
  - create, list, get, and replace (`If-Match`)
  - `retire` and `reactivate`
- **Internal taxonomy,** under `/internal/v1/catalog/categories`:
  - create, list, get, and replace (`If-Match`)
  - `retire` and `reactivate`
- **Health:** `/health`, `/health/live` and `/health/ready`.

## 8. Discovery

- **Public queries** return `Published` products only. Responses carry:
  - only active variants
  - only active categories
  - the brand (ID, name, slug) only if active
  - no status, version, audit subjects or idempotency data
- **Filters:**

  | Filter | Who | Rule |
  |---|---|---|
  | `q` | all | 2 to 100 characters, text index (ADR-024 section 9) |
  | `categoryId` | public | includes descendants |
  | `brandId` | all | |
  | `tenantId` | public | |
  | `attr=key:value` | public | up to 5 pairs, each an `$elemMatch` on key and normalized value |
  | `status` | merchant | |
  | `sku` | merchant | exact, case-insensitive |

- **Sorts:** `newest` (the default) and `oldest`. Public sorts use `publishedAt`, merchant sorts use `updatedAt`. Every sort ends with `_id`.
- **Pagination:** `page` and `pageSize` (20 by default, at most 100). Public queries allow at most 100 pages. Every query has a 5-second server time limit.
- **Limitations:**
  - no relevance ranking
  - no stemming, prefix or fuzzy matching
  - several terms match with OR
  - text queries sort in memory after the text match
  - `totalCount` costs one count per page

## 9. Integration events

None in Phase 4 (ADR-024 section 13). A future slice needs:

- a single-node replica set in Compose and CI
- a MongoDB outbox design under ADR-023
- the event contracts
- the `catalog.events` topic

The trigger is the first consumer: Cart, Order or Inventory.

## 10. Milestones and progress

| # | Milestone | Status |
|---|---|---|
| 1 | Design decisions (ADR-024, ADR-025, this document) | done |
| 2 | `BuildingBlocks.Authentication`, Tenant & Merchant moved to it | done |
| 3 | Tenant & Merchant membership verification endpoint | done |
| 4 | Catalog service foundation and architecture rules | done |
| 5 | Domain model | done |
| 6 | MongoDB persistence, indexes, readiness, CI MongoDB | done |
| 7 | Merchant authorization through Tenant & Merchant | done |
| 8 | Merchant product and brand APIs | done |
| 9 | Platform category taxonomy | planned |
| 10 | Public discovery | planned |
| 11 | Gateway route | planned |
| 12 | Documentation, operations and evidence | planned |

## 11. Implementation decisions and deviations

| # | Topic | Decision | Relation to the ADRs |
|---|---|---|---|
| D1 | How a duplicate key is identified | The MongoDB driver (3.14.1) does not expose the server's `keyPattern` for a write error. The store therefore reads the **index name** from the server message (`index: <name> dup key`) and accepts only one of the five known unique index names. An unknown name is an error, never a guess. The message contains stored values and is never logged or returned. | ADR-024 section 11 says duplicates are translated "by the index that rejected the write, never by the error message". The intent holds: the outcome is decided by the rejecting index, and no message content is used or exposed. The index name is taken from the message text because the driver offers no other source. |
