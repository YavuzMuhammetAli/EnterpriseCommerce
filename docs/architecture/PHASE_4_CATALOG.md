# Phase 4: Catalog Service

The Catalog microservice (`EnterpriseCommerce.Catalog`). It owns product content, the platform category taxonomy, tenant brands, variants with their list prices, product attributes, media references, the publication lifecycle and catalog discovery.

- **Decisions:**
  - [ADR-024](../decisions/ADR-024-catalog-service-design.md): the Catalog design
  - [ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md): merchant authorization through Tenant & Merchant, and shared token validation
- **Contract:** [CATALOG_API_CONTRACT.md](../api/CATALOG_API_CONTRACT.md).
- **Operations:** [CATALOG_OPERATIONS.md](../operations/CATALOG_OPERATIONS.md).
- **Status:** **complete** (2026-11-24). Every milestone of [section 10](#10-milestones-and-progress) is done, and the evidence is in [section 12](#12-test-evidence).
  - **Verification level.** Tested locally only, against MongoDB 8.0, SQL Server 2022, LocalDB, Kafka and Redis. It is not production-verified, and the commits are not yet CI-verified: GitHub Actions runs only after the owner pushes.
  - **First CI run.** It failed one Catalog test on a signing-key lifetime defect, and one Firefox browser case timed out. The defect is fixed and the browser case did not reproduce: [section 17](#17-ci-findings-after-completion). A green CI run is still open.
  - **Open items.** Production gates: [section 16](#16-production-gates-open). Deferred capabilities: [section 15](#15-deferred-capabilities).

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
| 9 | Platform category taxonomy | done |
| 10 | Public discovery | done |
| 11 | Gateway route | done |
| 12 | Documentation, operations and evidence | done |

## 11. Implementation decisions and deviations

| # | Topic | Decision | Relation to the ADRs |
|---|---|---|---|
| D1 | How a duplicate key is identified | The MongoDB driver (3.14.1) does not expose the server's `keyPattern` for a write error. The store therefore reads the **index name** from the server message (`index: <name> dup key`) and accepts only one of the five known unique index names. An unknown name is an error, never a guess. The message contains stored values and is never logged or returned. | ADR-024 section 11 says duplicates are translated "by the index that rejected the write, never by the error message". The intent holds: the outcome is decided by the rejecting index, and no message content is used or exposed. The index name is taken from the message text because the driver offers no other source. |
| D2 | Variant management | Variants are managed through the product `PUT` (the complete variant list, with stable IDs), not through separate variant endpoints. One write replaces the whole product consistently, and the publication rules see the whole list. | Within ADR-024 section 3 |
| D3 | Sorting | Only `newest` and `oldest`: by publication time publicly, by update time for merchants. Name and price sorts were left out: they need extra indexes, and a price sort would need a policy for multi-currency catalogs. | Within ADR-024 section 9 |
| D4 | Brand and category creation | No `Idempotency-Key`. A retried creation meets the slug's unique index and answers `409 slug-taken`; the client then reads the existing resource. Product creation, which has no natural unique key before the slug is settled, is idempotent per key. | Within ADR-024 section 14 |
| D5 | The public read side | The public queries are separate (filters, indexes, published-only) and project documents through the same mapping as the commands, then into the public view. There is no separate read model or collection. | ADR-024 section 12 (selective CQRS) |
| D6 | Anonymous storefront browsing | The storefront BFF forwards `/api/**` only with a session (BFF_API_CONTRACT.md), so anonymous discovery through it needs a BFF design (an anonymous pass-through without a token). It is deferred. Public discovery is served on the Gateway API host. | ADR-016 unchanged |
| D7 | Test project placement and containers | The session brief named `tests/Services/Catalog/` and a Compose service for Catalog. The repository convention is `tests/{Service}/` (SOLUTION_STRUCTURE), and Compose runs infrastructure only, with no application containers or Dockerfiles yet (deploy/docker/README). Catalog follows the repository, and uses the existing `mongodb` profile. | Repository conventions |
| D8 | An unknown body member | It answers `400` with the generic RFC 9110 problem type, as in Tenant & Merchant (the JSON reader refuses the body before validation runs). Field validation errors use `validation-failed`. | Platform convention |

## 12. Test evidence

**Full solution** (2026-11-23/24, Release, `EC_REQUIRE_INFRA_TESTS=true`, environment in section 13; the counts include the two regression tests of [section 17](#17-ci-findings-after-completion)):

| Module | Tests | Notes |
|---|---|---|
| Catalog.UnitTests | 121 | domain rules, the authorizer and its meters, paging, search text, settings |
| Catalog.IntegrationTests | 122 | MongoDB persistence and indexes, the Tenant & Merchant client, the merchant, taxonomy and public APIs, security, unavailability, the 413 test on Kestrel |
| ArchitectureTests | 169 | includes the new MongoDB boundary and BuildingBlocks rules |
| Gateway.IntegrationTests | 85 | includes the Catalog route and the internal-path cases through it |
| TenantMerchant.UnitTests | 447 | |
| TenantMerchant.IntegrationTests | 219 | includes the 18 membership verification tests and the Kafka tests |
| Identity.UnitTests | 169 | unchanged |
| Identity.IntegrationTests | 166 | unchanged |
| Bff.UnitTests | 103 | unchanged |
| Bff.IntegrationTests | 63 | unchanged, includes the merchant flow through the real Gateway |
| Bff.BrowserTests | 18 | Chromium, Firefox and WebKit through Playwright |
| BuildingBlocks.Authentication.UnitTests | 11 | the caller claim tests, moved from Tenant & Merchant |
| **Total** | **1,693** | **1,693 passed, 0 failed, 0 skipped** |

**How it was run:**

- **The first full run (all modules in parallel, SQL Server on LocalDB)** had 20 failures in Identity, BFF and Tenant & Merchant integration tests: SQL login and command timeouts at LocalDB under the load of every module creating databases at once, plus one outbox health check reading `Degraded` under the same load. No Catalog test failed.
- **The rerun** used one module at a time (`--max-parallel-test-modules 1`), with no other change, and **passed completely**. No test, timeout or assertion was changed.
- **CI** uses a SQL Server container, not LocalDB.

**SQL Server 2022 (Compose, as CLAUDE.md requires):** Tenant & Merchant integration 218/218, BFF integration 63/63 and Identity integration 166/166 passed against the Compose SQL Server 2022.

**Other checks:**

- `dotnet build -c Release`: 0 warnings, 0 errors. NuGet audit is clean with MongoDB.Driver 3.14.1.
- The CI secret-scan expression over every test log: clean. Tracked files contain no private key and no local `.env` value.
- Every relative Markdown link and anchor resolves (a local check).
- `git diff --check`: clean. `docker compose config --quiet`: valid for every profile.
- **Manual smoke test** (real processes, Gateway on `https://localhost:7000`):
  - liveness and readiness `Healthy`, with 11 product indexes created
  - public product and category lists answer `200` through the Gateway
  - `/internal/v1/catalog/categories` answers `404` at the Gateway
  - a merchant route without a token answers `401`
  - while MongoDB was stopped: liveness `Healthy`, readiness `503`, the public list `503`, then readiness `Healthy` again after the restart
- **A defect found by the verification and fixed:** Catalog test databases of API fixtures were not dropped (commit `e8c5b7d`). After the fix, a run leaves no database behind.

## 13. Verification environment

- **Machine:** Windows 11, Docker Desktop on WSL2 (about 3.8 GB VM), .NET SDK 10.0.404.
- **Infrastructure:**
  - Compose MongoDB `mongo:8.0.38` on port 27018 (a local `mongod` holds 27017)
  - the session Redis (`bff` profile) and Kafka (`kafka` profile)
  - SQL Server through LocalDB, and Compose SQL Server 2022 for the Tenant & Merchant and Identity suites
- `EC_REQUIRE_INFRA_TESTS=true`, so no infrastructure test could skip.

## 14. Known limitations

- **Search is basic.**
  - Whole words only, with no stemming, prefix or fuzzy matching. Several words match with OR.
  - Text queries sort in memory after the text match.
  - There is no relevance ranking, and no performance claim is made.
- **`totalCount` costs one count per page.** Public paging stops at page 100.
- **Reference race.** A category or brand retired between a reference check and the write can still be assigned (ADR-024 section 11). Public views hide retired references, and the next update or publication revalidates.
- **Tenant status is invisible to Catalog.** A suspended tenant's published products stay publicly visible until tenant status reaches Catalog. Merchant writes are refused, because Tenant & Merchant's verdict requires an active tenant (ADR-025, Consequences).
- **Availability coupling.** Merchant management is unavailable while Tenant & Merchant or Identity's token endpoint is. Public discovery is not affected.
- **No category creation without a service client.** Until Admin exists, categories are created only by an allow-listed test or development client.
- **Single-node MongoDB.** No retryable writes and no transactions. Product creation retries are safe through the idempotency key.

## 15. Deferred capabilities

| Capability | Why deferred | Prerequisite |
|---|---|---|
| Catalog integration events (for example product published or price changed) | No consumer, and no transactional outbox on a standalone MongoDB (ADR-024 section 13) | A single-node replica set in Compose and CI, a MongoDB outbox design under ADR-023, the first consumer (Cart, Order or Inventory), and `catalog.events` in the topic manifest |
| Image upload and object storage | No approved media store, and no paid cloud dependency (ADR-024 section 8) | A storage decision (an ADR), content scanning, and image processing |
| Moving categories | A subtree ancestry update needs multi-document atomicity | A replica set, or a designed migration process |
| Discounts, price lists, tax and multi-currency pricing | Not in the MVP | The checkout design (Order) |
| Anonymous browsing through the storefront BFF | Needs a BFF anonymous pass-through design (D6) | A BFF design change |
| Internal product reads for Cart and Order (snapshots) | Resolved for Inventory by ADR-027 and for Cart by [ADR-029](../decisions/ADR-029-cart-service-design.md) section 5 (Cart uses the public product read, no internal read). Still open for Order. | The Order phase |
| A local membership projection | Membership events do not exist | Membership events, a consumer and an inbox (ADR-014 rule 3, ADR-023 section 7) |
| Advanced search (Elasticsearch, OpenSearch) | Post-MVP (PROJECT_DECISIONS section 11) | An architecture review and an ADR |

## 16. Production gates (open)

- **MongoDB:**
  - a dedicated least-privilege `catalog` user
  - TLS
  - a replica set
  - backup and restore procedures
  - index builds planned for large collections
- **Keys and configuration:**
  - the `svc-catalog` private key in a managed key store
  - the deployed Identity client registration and Tenant & Merchant allow-list entry
- **Routing:** deployed Gateway routes and host names, and TLS between the Gateway and Catalog.
- **Operations:** a metrics exporter and alerting (the observability phase).
- **Performance:** load and performance measurement of discovery queries on realistic data volumes.
- **CI:** a green GitHub Actions run of the Phase 4 commits, which only the owner's push can produce. Local evidence is not CI evidence.

## 17. CI findings after completion

The first GitHub Actions run of the Phase 4 commits (2026-11-24) failed in two places. This section records the root cause, the fix and the evidence.

### F1: a disposed signing key shared through the process-wide signer cache (fixed)

**Symptom.** `MembershipVerifierTests.AnActiveMember_IsVerifiedWithTheirRole_AndTheCorrelationIdTravels` failed. The svc-catalog client assertion could not be signed:

- `MembershipUnavailableException` "No service token for Tenant & Merchant"
- inner `ObjectDisposedException` on `RSAOpenSsl`
- log event 4201

The verification failed closed, as designed.

**Root cause.**

1. **Ownership.** `CatalogServiceTokenProvider` is a singleton per host. It loads its own `RSA` from the key file and disposes it with the host. That ownership is correct.
2. **The shared cache.** The assertion was signed through `CryptoProviderFactory.Default`, a process-wide cache of signature providers in Microsoft.IdentityModel. The cache key is the key's `InternalId`, a thumbprint of the **public** key. Two `RsaSecurityKey` instances loaded from the same file therefore share one cached signer, and that signer holds the RSA of the first host that signed.
3. **The trigger.** `MembershipVerifierTests` runs several hosts in one process on the fixture's single key file: the shared host, plus per-test hosts it disposes. When a per-test host signed first and was disposed, the shared host's next signing used the disposed RSA.
4. **Why only CI.** xUnit's order within a class depends on the platform. On Windows the long-lived host signed first, so the class passed. In CI's Linux order it failed.

**Classification.** This is a lifetime defect in production code, not in the test fixture. The class disposed a key that a process-global cache it never opted out of still referenced. It surfaces whenever two hosts in one process use the same key file. The fixture's own key is disposed only after all of its hosts, so the fixture was not at fault.

**Fix.** Each token provider's `RsaSecurityKey` now has a `CryptoProviderFactory` of its own with `CacheSignatureProviders = false`. Its signer therefore always uses the RSA the provider owns.

What stays the same:
- The RSA is still disposed with the provider.
- The access token (never a verdict) is still cached.
- Every failure still fails closed.
- No exception is caught or suppressed.

A signer is now built per assertion, which happens about once every four minutes per host.

The same code in Tenant & Merchant's `IdentityServiceTokenProvider` (the template the Catalog provider was copied from) got the same fix. No test order triggered it there yet.

| Commit | Change |
|---|---|
| `9b36fa6` fix(catalog) | `CatalogServiceTokenProvider` and the regression test `MembershipVerifierTests.AnotherHostsDisposedKey_NeverBreaksSigning` |
| `707aa0d` fix(tenant) | `IdentityServiceTokenProvider` and the regression test `InvitationAcceptanceTests.AnotherHostsDisposedKey_NeverBreaksSigning` |

Each regression test runs in any order. It signs with one host, disposes that host, then signs twice with a second host on the same key file (one `401` forces a renewal).

**Evidence:**

- **Before the fix:**
  - Both regression tests failed with `ObjectDisposedException`, on Windows (`RSABCrypt`).
  - The original CI failure was reproduced exactly in Linux: SDK 10.0.301 container, CI's checkout path, `git archive` of `7a3aa8a`. `AnActiveMember_…` failed in 3 of 3 runs of the class.
- **After the fix:**
  - In the same Linux container, `MembershipVerifierTests` passed 25/25 in 3 of 3 runs.
  - On Windows, the class passed 25/25 in 20 of 20 runs, the original test passed 20 of 20 runs alone, and each regression test passed 10 of 10 runs.
  - Catalog: unit 121/121, integration 122/122, against MongoDB.
  - Tenant & Merchant: unit 447/447. The integration suite passed 219/219 in the full run, with Kafka and the Compose SQL Server 2022.
- **Full solution, configured as in CI:**
  - Release, `EC_REQUIRE_INFRA_TESTS=true`, Compose SQL Server 2022, MongoDB, the session Redis and Kafka
  - 1,693 tests: 1,675 passed, 0 failed, and 18 skipped (the browser tests, which CI runs in a separate job, as here)
  - Build: 0 warnings
  - The CI secret scan over all logs: clean

**The same latent pattern, unchanged.** Identity's `NotificationSecurityEmailDispatcher` and the BFF's `ClientAssertionFactory` also sign with an RSA they own and dispose, through the default cache. No test or deployment runs two of their hosts on one key file, and Phase 2.3 is closed, so they are unchanged. A host that ever runs two instances on one key file, in tests or in production, needs the same fix.

### F2: a Firefox login case timed out (not reproduced)

`BrowserFlowTests.Login_SetsTheAcceptedCookies_AndNoTokenReachesTheBrowser(engine: "firefox")` timed out after 30 s in `WaitForURLAsync("/")`, after the password was submitted.

- **Where it hung.** The stack ends in `WaitForLoadStateAsync`: the address already matched, but Firefox never reported the `load` event.
- **Not caused by Phase 4.** No Phase 4 commit touched the BFF, Identity or the browser tests.
- **Reproduction attempts.** The browser suite passed 18/18 in 3 of 3 separate runs on Windows (Compose SQL Server and the session Redis).
- **The Linux attempt.** It also passed 18/18 in 3 of 3 runs in Linux: SDK 10.0.301 container, CI's checkout path, Playwright browsers installed with `--with-deps` as in CI.
- **Outcome.** The failure did not reproduce, so the cause is unknown and no code was changed. It is recorded as an open CI flake of the Phase 2.2 browser suite. If it recurs, the next step is a Playwright trace of the Firefox case.
