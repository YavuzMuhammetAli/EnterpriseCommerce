# ADR-024: Catalog Service Design

- **Status:** Accepted
- **Date:** 2026-11-23
- **Phase:** 4 (Catalog)
- **Related:**
  - [ADR-003](ADR-003-database-per-service.md) (database per service)
  - [ADR-004](ADR-004-sql-server-and-mongodb-selection.md) and [ADR-011](ADR-011-per-service-data-store-assignment.md) (Catalog uses MongoDB)
  - [ADR-009](ADR-009-selective-cqrs.md) (selective CQRS)
  - [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md) and [ADR-023](ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) (outbox)
  - [ADR-014](ADR-014-logical-tenant-isolation.md) (tenancy)
  - [ADR-025](ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md) (merchant authorization)
- **Design and evidence:** [PHASE_4_CATALOG.md](../architecture/PHASE_4_CATALOG.md).

## Context

SERVICE_CATALOG gives Catalog these responsibilities:

- product information and descriptions
- categories and product attributes
- the product publication lifecycle
- catalog search and retrieval

ADR-011 assigns it MongoDB. Nothing below the service boundary was designed. The repository had no MongoDB code, no MongoDB client package and no MongoDB tests.

Four facts constrain the design:

- **The Compose MongoDB is standalone.** It runs `mongo:8.0.38` with no `--replSet`, so **multi-document transactions are unavailable**. Only single-document writes are atomic.
- **No consumer of Catalog data exists.** Cart, Order and Inventory are not built.
- **There is no platform administrator authorization.** Admin is not built, and Identity roles are not authoritative (ADR-015).
- **Membership is owned by Tenant & Merchant and is not published as events** (PHASE_3_TENANT_MERCHANT.md section 7).

The project owner decided on 2026-11-23, when approving the Phase 4 plan, that:

- categories are a platform-wide taxonomy
- brands are tenant-scoped

## Decision

### 1. Client library: MongoDB.Driver, in Catalog.Infrastructure only

- **The package.** `MongoDB.Driver` 3.14.1 is the official MongoDB client for .NET (Apache-2.0). It is the client of an approved store (ADR-004), not a new technology category.
- **Where it may be referenced.** Only `EnterpriseCommerce.Catalog.Infrastructure` may reference it. An architecture test allow-lists MongoDB packages to that project, as `MessagingBoundaryTests` does for Kafka.
- **No mapping framework and no generic repository.** Infrastructure holds explicit persistence document classes and hand-written mappers. Domain types carry no BSON attributes.
- **Serializers:**
  - GUIDs use the standard representation, registered once with `TryRegisterSerializer`.
  - Money is `Decimal128`.
  - Times are BSON dates in UTC. Domain times are truncated to milliseconds, which is BSON's precision.

### 2. Tenant ownership

- **Tenant-owned documents.** A product and a brand each carry an explicit `tenantId`.
- **How it is set.** It is set only at creation, from the route's tenant after authorization (ADR-025). No update contract contains it, and every query and write filters on it.
- **Categories are platform-owned**, so they carry no tenant.

### 3. Product aggregate, variants and SKU scope

- **One product is one document** (the `products` collection), together with its variants, attributes and media. Every product change is therefore one atomic single-document write.
- **Optimistic concurrency.** A `version` counter guards every replacement. A replacement whose expected version no longer matches changes nothing.
- **Identifiers.**
  - Public identifiers are GUIDs (version 7), stored as `_id`.
  - The slug is unique per tenant: 3 to 100 characters of `a-z` and `0-9` in hyphen-separated groups, never normalized.
- **Variants.**
  - **Every sellable unit is a variant.** A product "without variants" has exactly one. A draft may have none.
  - A variant has:
    - a stable ID
    - a SKU
    - option values
    - a price
    - an active flag
  - The option sets of a product's variants are distinct.
- **SKU uniqueness.**
  - **SKUs are unique per tenant, across all of its products**, compared case-insensitively. Inventory and Order will identify a sellable unit by tenant and SKU.
  - **Across documents,** a unique multikey index enforces the rule. It is partial on the existence of a SKU key, so drafts without variants do not collide.
  - **Within one document,** MongoDB does not enforce unique array elements, so the domain enforces it.
- **No inventory data.** Products and variants carry no stock quantity, availability or reservation data. Inventory owns all of it.

### 4. Pricing ownership

- **Catalog owns the list price.** PROJECT_DECISIONS section 5 ("Catalog price changes never modify historical orders") implies Catalog owns it, and no Pricing service exists. Order takes its own immutable snapshot at checkout.
- **Representation.**
  - One price per variant.
  - The amount is a decimal greater than 0 and at most 1,000,000,000, with at most two decimal places.
  - The currency is one of `EUR`, `USD`, `GBP` or `TRY`.
- **One currency per product.** All variants of a product use the same currency.
- **Deferred:** discounts, price lists, tax and multi-currency pricing.

### 5. Lifecycle and publication

- **States:** `Draft`, `Published` and `Archived`.
- **Transitions:**

  | Command | From | To |
  |---|---|---|
  | publish | Draft | Published |
  | unpublish | Published | Draft |
  | archive | Draft or Published | Archived |
  | restore | Archived | Draft |

- **Repeating a command** whose target state is already in effect changes nothing and succeeds, as Tenant & Merchant's state commands do.
- **Every other transition is refused** with `invalid-transition`. Archived products are read-only, except for restore.
- **Publication requirements:**
  - at least one category, and every referenced category active
  - the brand, if set, active
  - at least one active variant
- **Updates to a published product are revalidated.** An update that would break a requirement is refused. A product is never silently unpublished.
- **Visibility.** Public queries return only `Published` products. Draft and archived products are visible to authorized merchant users only.
- **No approval workflow.** No accepted decision requires one.

### 6. Categories: a platform taxonomy (owner decision)

- **One marketplace-wide tree,** with slugs unique across the platform and at most 5 levels.
- **Writes** go only through `/internal/v1/catalog/categories`, by allow-listed `svc-` service clients:
  - the audience `catalog-api`
  - the scope `svc.catalog.taxonomy`
  - an allow-list that is empty by default

  The intended caller is the future Admin service. Merchants assign existing categories to their products.
- **Reads** are public.
- **The parent is fixed at creation** and must already exist and be active. A parent is therefore always older than its child, so the tree is acyclic by construction, and self-parenting is impossible. Moving a category is deferred: it changes the ancestry of a whole subtree, and so needs multi-document atomicity.
- **No hard delete:**
  - **Retiring** a category is refused while it has active children.
  - **Reactivating** one requires an active parent.
  - **References are never orphaned.** Products keep their references, so nothing is ever left dangling.
  - **Effects of a retired category:**
    - it cannot be newly assigned
    - it blocks publication
    - it is omitted from public product views
- **Ancestry.** Because ancestry never changes, a product stores the ancestors of its categories. A category filter therefore includes descendants without a tree walk.

### 7. Brands: tenant-scoped (owner decision)

- **A brand belongs to one tenant.** Its slug is unique per tenant, and it is managed by `MerchantOwner` and `MerchantAdmin` through the merchant API.
- **Products reference brands of their own tenant only.**
- **No hard delete,** for the same reason as categories. A retired brand cannot be newly assigned and blocks publication.
- **Brand names are not copied into products.** Public views resolve them with one lookup per page.

### 8. Media boundary

- **References and metadata only.** Catalog stores media references: an ID, a URL, alt text and a content type (`image/jpeg`, `image/png`, `image/webp` or `image/avif`).
- **No binaries.** Catalog never stores binary media, and Phase 4 has no upload pipeline and no object store.
- **URL rules:**
  - absolute `https`, at most 2,048 characters
  - no user information and no fragment
  - not an IP literal, and not `localhost` or a `.localhost`, `.local` or `.internal` host
- **The service never fetches a media URL**, so it has no server-side request forgery surface. Browsers load the images.
- **Deferred:** an upload pipeline with object storage, content scanning and variants of the image.

### 9. Search, filtering and pagination

- **MongoDB only.** Search uses no external search engine (Elasticsearch, OpenSearch) in this phase.
- **Text query.** `q` uses one MongoDB text index over the name, short description and description, with the language `none`.
  - Terms are matched as whole words, with no stemming and no prefix matching.
  - Several terms are combined with OR.
  - The characters `-` and `"` are removed, so a query cannot express negation or phrases.
  - **No relevance ranking or performance is claimed.**
- **Exact filters:**
  - category, including descendants
  - brand
  - tenant
  - up to 5 attribute pairs, each matched with `$elemMatch`
  - for merchants, status and SKU
- **Sorting** is a closed set: `newest` (the default) and `oldest`, by publication or update time, with `_id` as the tie-breaker.
- **Pagination** follows the platform convention (`page`, `pageSize` 20 by default and at most 100). Public queries allow at most 100 pages.
- **Bounded queries.** Every query has a server time limit. Clients never pass filter or sort expressions.

### 10. Indexes

- **Indexes follow the query patterns.** They are listed with their justification in PHASE_4_CATALOG.md section 5.
- **Uniqueness is enforced by unique indexes**, not by application checks alone:
  - product slug per tenant
  - SKU per tenant
  - creation idempotency key per tenant and user
  - category slug
  - brand slug per tenant
- **Creation:**
  - **Startup.** A background initializer creates the indexes at startup, idempotently, with explicit names.
  - **Writes wait for the indexes.** The service refuses writes, and is not ready, until every index exists, so uniqueness is never unprotected.
  - **Conflicts stop the service.** An index definition conflict is a permanent error: it is logged, and the service stays not ready.

### 11. Consistency guarantees and limitations

- **Single-document atomicity and optimistic concurrency.** No multi-document transaction is used or required, and no distributed transaction.
- **Duplicate-key errors** are translated by the index that rejected the write, never by the error message.
- **References** to categories and brands are validated before the write. A category or brand retired between the check and the write can still be referenced. The effect is bounded:
  - public views omit retired categories
  - the next publish or update revalidates the reference
- **Retryable writes** need a replica set. On the standalone server they are not used. Clients retry product creation with the same idempotency key.

### 12. Selective CQRS

- **Commands** load an aggregate, apply a domain operation and save it, through one store interface per aggregate.
- **Public discovery** uses a separate read-side query service, which projects documents straight into response models.
- **No mediator and no generic repository.**

### 13. Integration events: none in Phase 4

**Catalog publishes no integration events in Phase 4.** There are three reasons:

1. **No consumer exists.**
2. **No transactional outbox is possible on the current topology.** ADR-012 requires the outbox to be written in the business transaction, and a MongoDB outbox needs a multi-document transaction (a replica set) or an outbox embedded in each document with its own relay design. Publishing from HTTP handlers or after the write would lose or invent events, which ADR-012 rejects.
3. **ADR-023 is SQL Server-shaped.** Its relay (application locks, `READPAST`, identity ordering) does not carry over to MongoDB.

**Required future work, in the slice of the first consumer:**

- a single-node replica set in Compose and CI
- a MongoDB outbox design under ADR-023
- the event contracts
- the topic `catalog.events` in the Kafka topic manifest

### 14. HTTP conventions introduced by Catalog

- **Optimistic concurrency on full replacements:**
  - A full-replacement `PUT` (product, brand, category, media list) requires `If-Match` with the entity tag returned by reads.
  - A missing tag answers `428`, a stale one `412`, and a race lost on the server `409 concurrency-conflict`.
- **State commands** (publish, retire, and so on) need no entity tag. They are safe to repeat.
- **Request bodies** are limited to 256 KiB.

## Consequences

- **Positive:**
  - Atomic product changes without transactions.
  - A data model that matches product-shaped reads.
  - Uniqueness that holds under concurrency.
  - No inventory, cart or order data in Catalog.
- **Positive:** the platform taxonomy supports marketplace-wide browsing. Tenant-scoped brands rule out brand squatting across merchants.
- **Negative:**
  - **Categories cannot be moved yet.** Moving one needs a subtree update.
  - **No outbox yet.** Catalog cannot feed other services through events until a replica set and a MongoDB outbox exist.
- **Negative: merchants cannot create categories.** Until Admin exists, a taxonomy is created only by an allow-listed service client.
- **Negative: search is basic.** It has whole-word, OR-combined matching with no relevance ranking claim. Advanced search stays post-MVP (PROJECT_DECISIONS section 11).
- **Negative: no media upload.** Merchants must host images themselves.

## Alternatives considered

- **Variants as separate documents:** this needs multi-document transactions for any product-wide invariant (one currency, a unique option set, publication requirements). Rejected.
- **SKU unique per product, or globally:**
  - Per product is too weak for Inventory and Order, which identify a unit by tenant and SKU.
  - Globally would let one merchant block another's SKU.
  - Rejected.
- **Tenant-scoped categories:** these give no marketplace-wide browsing (owner decision).
- **Platform brands:** a brand registry would need an operator before a merchant could sell a new brand (owner decision).
- **Hard delete with a reference check:** without transactions, a product could gain the reference between the check and the delete and be orphaned. Rejected in favour of retiring.
- **Movable categories with an ancestor check:** concurrent moves could still create a cycle without multi-document atomicity. Deferred.
- **Storing uploaded images in MongoDB (GridFS):** it mixes binary storage with catalog data and is not an approved media store. Rejected.
- **A replica set and a MongoDB outbox now:** this has no consumer, and would build the relay before its design. Deferred (section 13).
- **Elasticsearch or OpenSearch:** new infrastructure, and advanced search is post-MVP. Rejected for this phase.
