# Phase 3: Tenant & Merchant

The design and the implementation progress of the Tenant & Merchant service. It works within [ADR-014](../decisions/ADR-014-logical-tenant-isolation.md) (logical tenant isolation) and [ADR-020](../decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md) (the `TenantMerchant` name, and one merchant per tenant). The API is specified in [TENANT_MERCHANT_API_CONTRACT.md](../api/TENANT_MERCHANT_API_CONTRACT.md).

- **Status:** Phase 3 is authorized and implemented in small slices.
  - **Slice S1** (service foundation and tenant provisioning) is implemented and tested locally.
  - **Slice S2** (merchant membership management) is implemented and tested locally. Its design is in [PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md](PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md).
  - **Slice S3** (self-service merchant onboarding) is implemented and tested locally. Its design is in [PHASE_3_S3_SELF_SERVICE_ONBOARDING.md](PHASE_3_S3_SELF_SERVICE_ONBOARDING.md), its decisions in [ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md).
  - Later slices are not started.
- **Verification levels used below:**
  - **Implemented:** in the code.
  - **Tested locally:** automated tests against a real SQL Server 2022 (Compose) and LocalDB. A manual smoke run with a real Identity token is noted where it applies.
  - **Production gate:** required before a production-like deployment, and not done.

## 1. Inputs and gaps

- The original Phase 0.1 (service boundaries and event catalog), Phase 0.2 (database design) and Phase 0.3 (API) documents are not in the repository. [IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md) already notes this for Phase 0.3. As a result:
  - There was no tenant schema, no tenant API contract and no event catalog to follow. `docs/events` defines no topics, so a `tenant.events` topic does not exist.
  - The schema, the contract and the rules below were designed in Phase 3 S1. The open questions were decided by the owner on 2026-11-21 (section 9).
- [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md) forbids outbox code before its design is approved. The Kafka client library, topic naming and schema format are still open (PROJECT_DECISIONS section 13).

## 2. Domain model

**Tenant.** One merchant organization, and the ADR-014 isolation boundary:

- `Id`: a GUID (version 7).
- `Slug`: the immutable handle; rules in section 4.
- `Status`.
- `CreatedAt`, in UTC.
- `Origin` (S3): `Provisioned` or `SelfService`; for a self-service tenant also `SelfServiceSubjectId`, the creating subject.
- The provisioning record: the client, the idempotency key, the request hash and the actor reference. A self-service tenant records the merchant BFF's client, the user's key and the subject here (S3).

S1 has the status `Active` only. Suspension and closure come with Admin tenant management.

**Merchant profile.** Exactly one per tenant, written in the tenant's transaction: `DisplayName`, `CreatedAt`, `UpdatedAt`.

**Membership.** A separate aggregate, because a tenant's memberships are unbounded:

- `Id`, `TenantId`.
- `SubjectId`: the Identity `sub`, an opaque external reference. No account data is stored.
- `Role`, `Status`, `CreatedAt`, `StatusChangedAt`.

## 3. Merchant roles, permissions and membership rules

Roles are owned and enforced here and never appear in tokens (ADR-017 section 2).

| Role | Meaning | `ViewTenant` | `ViewMemberships` |
|---|---|---|---|
| `MerchantOwner` | Owns the merchant organization | yes | yes |
| `MerchantAdmin` | Administers it | yes | yes |
| `MerchantStaff` | A merchant employee | yes | no |

S1 defined only the read permissions its endpoints needed. **S2** adds the write permissions and the per-target rules; the complete matrix is in [PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md section 3](PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md#3-role-and-permission-matrix).

**Rules:**

- **Owner rights only with a new tenant.** `Membership.CreateOwner` is the only way to `MerchantOwner`. In S1 only trusted tenant provisioning called it. Since S3, self-service onboarding also calls it, for the caller of a tenant it creates ([ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md), amending ADR-021 section 4). No operation makes anyone an owner of an existing tenant.
  - `CreateMember` refuses `MerchantOwner`, and `ChangeRole` to `MerchantOwner` returns `OwnerRoleReserved`.
  - Ownership transfer is a later, separately designed operation.
- **Status transitions:** `Active` ⇄ `Suspended`, and `Active` or `Suspended` → `Removed`.
  - `Removed` is terminal. A later membership of the same subject is a new membership.
  - An invalid transition is an outcome (`InvalidTransition`), not an exception.
- **The last active owner** cannot be suspended, removed or demoted (`LastActiveOwner`).
  - In S1 this is a pure domain rule with unit tests.
  - **Enforced under concurrency since S2:** every membership change counts the active owners under a per-tenant write lock, and a database trigger refuses any change that would leave no active owner ([PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md section 6](PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md#6-concurrency-and-the-last-active-owner-guarantee)).
- **Only active members** see anything. A suspended or removed member is treated like a non-member.

## 4. Provisioning (S1)

A trusted service caller (Admin, in future) provisions a tenant for an existing Identity subject. There is **no public or self-service path in S1**.

The accepted self-service onboarding of PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.1.2 builds on the same model. It is implemented in **S3** (`POST /api/v1/tenants`, [PHASE_3_S3_SELF_SERVICE_ONBOARDING.md](PHASE_3_S3_SELF_SERVICE_ONBOARDING.md)). Merchant applications and review are deferred to the Admin phase.

**Slug rules:**

- 3 to 63 characters of `a-z`, `0-9` and single `-`, starting and ending with a letter or digit.
- Unique and immutable.
- Never normalized: a non-conforming value is rejected, not changed.

**Idempotency** (closes [IDENTITY_API_CONTRACT.md section 4](../api/IDENTITY_API_CONTRACT.md#4-remaining-open-items) item 2 for this endpoint):

- `Idempotency-Key` is required, and unique per calling client.
- **It is stored on the tenant row and kept for the tenant's lifetime.** There is no separate store and no expiry.
- The request hash is SHA-256 over a versioned, length-prefixed encoding of the validated fields.
- Outcomes:
  - The same key with the same request returns `200` and the original tenant.
  - The same key with a different request returns `422`.
  - A different key with a taken slug returns `409`.

**Flow:**

1. Validate the fields.
2. Look up the key.
3. Check the slug.
4. Insert the tenant, merchant profile and owner membership in **one** `SaveChanges`, which is one transaction.

If a unique constraint rejects the insert, or the slug was taken between the lookups, the key is read again. A concurrent retry of the same request is therefore answered as a replay, and only a genuinely different tenant gives `409`. The SQL Server concurrency test found this race (a retry missed the key, then saw its twin's slug); a unit test now covers it.

**Residual risk:** the service cannot verify that `ownerSubjectId` exists in Identity. The allow-listed caller is trusted to pass a real subject.

## 5. Persistence

One SQL Server database per service (ADR-003, ADR-011), schema `dbo`, created by the EF Core migration `InitialTenantMerchantSchema`.

| Table | Key and constraints |
|---|---|
| `Tenants` | PK `Id`; unique `UX_Tenants_Slug` (`varchar(63)`); unique `UX_Tenants_ProvisionedByClientId_ProvisioningKey` (filtered on `Origin = 'Provisioned'` since S3); unique `UX_Tenants_SelfServiceSubjectId_ProvisioningKey` (filtered on `Origin = 'SelfService'`, S3); `CK_Tenants_Status`, `CK_Tenants_Origin`, `CK_Tenants_Origin_SelfServiceSubject` (S3); `rowversion` |
| `MerchantProfiles` | PK and FK `TenantId` → `Tenants` (same database); `rowversion` |
| `Memberships` | PK `Id`; FK `TenantId` → `Tenants`; filtered unique `UX_Memberships_TenantId_SubjectId_Current` on (`TenantId`, `SubjectId`) `WHERE Status <> 'Removed'`; `IX_Memberships_TenantId_CreatedAt` (listing order, initial owner, FK); `CK_Memberships_Role`, `CK_Memberships_Status`; `rowversion` |

- **No cross-service references:** there are no foreign keys or references to another service's database. Identity subjects are opaque strings.
- **Times** are `datetimeoffset` values from `TimeProvider`, in UTC.
- **Optimistic concurrency:** every table has a `rowversion` token, so that the updates of later slices fail on conflict instead of overwriting. Tested on memberships.
- **Failures:** every SQL Server failure becomes `503` with `Retry-After`. Nothing is reported as done.
  - There is no retry loop: the transaction is short and insert-only.
  - A caller retries with the same key, which is safe.
- **Migrations:** applied at startup only in Development, with `TenantMerchant:Database:ApplyMigrationsAtStartup=true`. Deployed environments apply them as a reviewed step.

## 6. Authentication and authorization

- **Token validation:** the standard JWT bearer handler, as in ADR-017 section 4: exact issuer, RS256, `at+jwt`, 30 seconds of skew, and audiences `tenant-merchant-api` and `ec-merchant`.
  - A **fallback policy** requires an authenticated caller on every endpoint that does not state a policy. Health is explicitly anonymous.
  - The role claim type is set to a claim no token carries, so a forged `role` claim can never become a role.
- **Provisioning policy:** `ec_ptype=service`, the service audience, the scope `svc.tenant-merchant.provisioning`, `sub == client_id` with the `svc-` prefix, and an allow-listed client.
  - The allow-list is empty by default.
  - Startup refuses an entry without the `svc-` prefix.
- **Member policy:** `ec_ptype=user`, the audience `ec-merchant`, a `sub`, and a non-`svc-` client.
  - Tenant access is then decided per request by `TenantAccessAuthorizer`, from this service's membership data only:
    - not visible gives `404`
    - forbidden gives `403`
  - Denials are logged as security events (event IDs 3100 and 3101, threat T20).
- **No generic authorization framework:** one authorizer, two policies and a role-permission table.

## 7. Integration events

**S1 publishes no integration event and contains no outbox code** (owner decision, section 9). Nor do S2 and S3. The reason is ADR-012: no outbox code before its design is approved, and the Kafka client, topic and schema conventions are still open.

The first event, provisionally `TenantProvisioned` on a tenant topic, is designed in the first event-flow phase together with the outbox. Until then:

- Other services cannot build local membership read models (ADR-014 rule 3).
- The membership data shared with other services, an open design item of PROJECT_DECISIONS section 7, stays undecided.

## 8. Slice progress

### S1: service foundation and tenant provisioning

| Item | Implemented | Tested locally |
|---|---|---|
| Four projects, solution, ports 5102 and 7102, Serilog, problem details, `X-Correlation-ID`, health | yes | yes |
| Domain: tenant, merchant profile, membership, roles, transitions, last-owner rule | yes | unit |
| Persistence: migration, constraints, `rowversion` | yes | SQL Server 2022 and LocalDB |
| Provisioning: atomic, idempotent, race-safe | yes | unit, SQL Server (rollback, 8-way concurrency) |
| Internal API and member API with authorization | yes | integration tests (security matrix, cross-tenant, roles, paging); a manual smoke run with a real Identity client-credentials token |
| Gateway Development route; `/internal/**` blocked | yes | Gateway tests |
| Architecture rules for the new service and ownership | yes | yes |
| Integration event and outbox | no (deferred) | |

**Tests at S1:**

- TenantMerchant: 112 unit tests and 76 SQL Server integration tests.
- Architecture: 58 tests. Gateway: 13 tests.
- The full solution suite: 756 tests, 0 failed, 19 skipped. The skips are the BFF browser tests and the Redis restart test, which need `EC_TEST_BROWSERS` and `EC_TEST_REDIS_RESTART_CONTAINER`. CI runs them.

**Manual smoke run (2026-11-21):**

- Identity issued a client-credentials token to a temporary `svc-` client with scope `svc.tenant-merchant.provisioning` and audience `tenant-merchant-api`: RS256, `typ: at+jwt`, `expires_in` 299 seconds.
- TenantMerchant accepted it:
  - `201` on provisioning
  - `200` on a replay
  - `200` on the internal read
  - `403` for the same token on the member API
- A user-token smoke run through the merchant BFF and the Gateway was not done (finding I29 covers the full local flow).

### S2: merchant membership management

Design, decisions and evidence: [PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md](PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md) and [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md).

| Item | Implemented | Tested locally |
|---|---|---|
| Invitations: create, list, read, cancel, accept; a single-use token stored as a hash and returned once | yes | unit, SQL Server |
| D14: acceptance requires Identity's verified-email verdict (new internal Identity endpoint, `svc-tenant-merchant` client) | yes | unit, SQL Server (stub Identity), Identity integration, end to end with the real Identity |
| Membership administration: suspend, reactivate, remove, role change, with the role matrix and the self-modification ban | yes | unit, SQL Server |
| The last-active-owner rule under concurrency: a per-tenant write lock, plus a trigger backstop | yes | SQL Server concurrency tests, including a mutation run without the lock |
| Append-only audit, atomic with each change | yes | SQL Server, including rollback on a failed audit write |
| The caller's tenants (`GET /api/v1/tenants`) | yes | SQL Server, end to end |
| Gateway Development route for acceptance; BFF and Gateway path | yes | Gateway tests, BFF end-to-end tests |
| Integration events and outbox | no (deferred, ADR-012) | architecture test |

### S3: self-service merchant onboarding

Design, decisions and evidence: [PHASE_3_S3_SELF_SERVICE_ONBOARDING.md](PHASE_3_S3_SELF_SERVICE_ONBOARDING.md) and [ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md).

| Item | Implemented | Tested locally |
|---|---|---|
| `POST /api/v1/tenants`: tenant, profile, the caller's owner membership and audit record in one transaction | yes | unit, SQL Server |
| The owner is always the token's subject; body owner fields refused; forged claims and headers ignored | yes | SQL Server, end to end |
| Idempotency per subject (a second key namespace; the S1 index filtered to provisioned tenants) | yes | unit, SQL Server, including two users with one key |
| Per-subject limit (default 1), serialized by a per-subject application lock | yes | unit, SQL Server races, a held-lock test, a mutation run without the lock |
| Migration `SelfServiceOnboarding`, with a guarded rollback | yes | SQL Server 2022 and LocalDB (inventory, backfill, down and up, refusal) |
| Gateway and BFF path (no code change) | yes | Gateway tests, BFF end-to-end tests |
| Integration events and outbox | no (deferred, ADR-012) | architecture test |

**Tests at S3 (local run of 2026-11-21, SQL Server 2022 and the session Redis, `EC_REQUIRE_INFRA_TESTS=true`, browsers on):**

| Project | Tests | S2 |
|---|---|---|
| TenantMerchant.UnitTests | 266 | 236 |
| TenantMerchant.IntegrationTests (SQL Server) | 162 | 115 |
| Gateway.IntegrationTests | 70 | 66 |
| Bff.IntegrationTests | 63 | 60 |
| Identity, BFF unit and browser, architecture | 533 | 533 (unchanged) |
| **Solution** | **1094** | 1010 |

- Release build: 0 warnings, 0 errors.
- No test was skipped.
- The TenantMerchant integration suite also passed against LocalDB.
- `dotnet test --solution EnterpriseCommerce.sln -c Release`: **1094 tests, 1094 passed, 0 failed, 0 skipped**.
- An earlier full run had one failure: the browser test `AfterAPasswordChange_TheBffReauthorizesSilently` (Chromium) timed out after 30 seconds waiting for network idle under the parallel load. The browser suite then passed 18 of 18 in three isolated runs, and the whole suite passed in the run above. S3 changes no BFF or Identity code.
- **CI** has not run on these commits; they are not pushed.

### Later slices (not started)

- Merchant applications and review (with Admin).
- Tenant suspension and closure (with Admin).
- Subscriptions and usage.
- The event flow (section 7).

## 9. Decisions made in S1

| Topic | Decision | Recorded in |
|---|---|---|
| Service name | `TenantMerchant` | ADR-020 |
| Tenant and merchant | One merchant per tenant; memberships are tenant-scoped | ADR-020 |
| Role catalog | `MerchantOwner`, `MerchantAdmin`, `MerchantStaff`, with the S1 rules of section 3 | this document |
| Integration event | None in S1 | this document, section 7 |
| Idempotency-Key retention (provisioning) | Kept on the tenant row for its lifetime | this document, section 4 |
| Pagination shape | `page` (1-based) and `pageSize` (20 by default, at most 100); `{ items, page, pageSize, totalCount }` | [docs/api/README.md](../api/README.md#conventions) |

Later decisions: S2 in [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md). S3 in [ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md): self-service onboarding with immediate activation, a per-subject limit (1 by default), per-subject idempotency, and an owner created only with its new tenant.

## 10. Findings, residual risks and production gates

**Findings:**

- **P3-1 (resolved 2026-11-21):** the Gateway's internal paths could be reached through a path-rewriting route.
  - **Root cause.** The Gateway blocked `/internal` only on the *incoming* path, before YARP applied a route's transforms. The Development route `/identity/{**catch-all}` removes its prefix, so `/identity/internal/...` passed the block and was forwarded to Identity as `/internal/...`.
    - The route has no host restriction and outranks the host catch-all routes, so this worked **on every host**.
    - Case variants, percent-encoded letters (`%69nternal`), dot segments and absolute-form request targets got through the same way.
    - Identity still required an allow-listed service token there, but the Gateway rule did not hold.
  - **Rule now ([src/Gateway/EnterpriseCommerce.Gateway/InternalPaths.cs](../../src/Gateway/EnterpriseCommerce.Gateway/InternalPaths.cs)):** no request whose path is internal is forwarded, on any host, by any route, in any environment.
    - A path is internal when its first segment is `internal` in any letter case, after repeated percent-decoding, treating `\` as a separator, and resolving empty, `.` and `..` segments.
    - The check runs twice. Once on the incoming path, before routing. Once on the outgoing path of every proxied request, as a global YARP request transform that runs after the route's own transforms.
    - It answers `404`, and nothing is sent to the destination.
    - Paths that merely contain `internal` later, such as `/internalized/...`, are still forwarded.
  - **Evidence:** `InternalPathExposureTests` runs the real Gateway on Kestrel with the committed Development routes, a recording backend behind every cluster, and raw HTTP/1.1 requests, so that no client-side URI normalization hides a path form. It covers:
    - the public Identity endpoints and the tenant member API, which are still forwarded
    - `/identity/internal/**` and `/internal/**` on seven hosts
    - 22 alternate path forms on three hosts
    - a probe of every committed route

    `EdgeRoutingTests` (Production environment) adds prefix-removing and rewriting routes and checks that their internal destinations are refused.

    Against the previous code, 37 of these 59 Gateway tests failed, 21 distinct requests reached the backend as internal paths, and the injected routes forwarded.
- **P3-2:** no service implements `X-Correlation-ID` except TenantMerchant. Identity does not echo or generate it yet.
- **P3-3:** host code is now duplicated between Identity and TenantMerchant:
  - problem details
  - health conventions
  - SQL error classification
  - JWT bearer setup (Identity uses OpenIddict validation)

  Extracting it into BuildingBlocks is a candidate. It needs a decision, and it would touch Identity.

**Residual risks:**

- The owner subject is not verified against Identity (section 4).
- Access tokens stay valid until they expire (5 minutes for service tokens). Membership changes take effect at once, because they are read from the database on every request.

**Production gates (open):**

- Deployed Gateway routes and host names. Admin's client registration and its allow-listing.
- An HTTPS metadata address on the internal network. Forwarded headers and host filtering, if the service is reached through the Gateway with public host names.
- A separate migration step for deployed environments.
- The integration event and outbox (section 7).
- Load or soak testing of the concurrency behaviour beyond the local 8-way tests.
