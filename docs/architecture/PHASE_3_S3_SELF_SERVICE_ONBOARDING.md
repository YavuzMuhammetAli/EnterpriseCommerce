# Phase 3 S3: Self-Service Merchant Onboarding

Slice S3 of the Tenant & Merchant service ([PHASE_3_TENANT_MERCHANT.md](PHASE_3_TENANT_MERCHANT.md)): a signed-in merchant-portal user creates a tenant and becomes its owner.

- **Decisions:** [ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md), which amends the owner rule of [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md) section 4.
- **API:** [TENANT_MERCHANT_API_CONTRACT.md section 4.7](../api/TENANT_MERCHANT_API_CONTRACT.md#47-create-a-tenant-self-service-onboarding).
- **Status:** implemented and tested locally (section 8). Production gates are in section 9.

## 1. How the scope was set

No document defined Phase 3 S3. PHASE_3_TENANT_MERCHANT.md section 8 listed four later slices without an order:

- onboarding and merchant applications
- tenant suspension and closure
- subscriptions and usage
- the event flow

The scope rests on this evidence:

- [PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.1.2](PHASE_2_2_AUTHENTICATION_FLOWS.md#512-merchant-user-registration-and-onboarding) (accepted) describes self-service onboarding.
- "Merchant onboarding" is an MVP item (PROJECT_DECISIONS section 11).
- The other three slices depend on Admin, Kafka or Notification, none of which exists, or on open decisions of PROJECT_DECISIONS section 13.

**Owner decisions (2026-11-21):**

1. S3 is self-service onboarding.
2. The tenant is active at once; merchant applications and review wait for Admin.
3. A configurable per-subject limit, 1 by default.

**In S3:**

- `POST /api/v1/tenants`: a tenant, its merchant profile, the caller's `MerchantOwner` membership and its audit record, in one transaction.
- Idempotency per subject, the per-subject limit, and their concurrency guarantees.
- The schema change, with a migration that keeps existing tenants as provisioned.
- The Gateway and BFF path, tested end to end. Neither needed a code change.

**Not in S3:**

- Merchant applications, vetting and review (Admin phase).
- Tenant suspension and closure, subscriptions and usage.
- Editing the merchant profile, leaving a tenant, ownership transfer.
- A merchant-portal user interface. The API is reached through the merchant BFF's `/api/**` forwarding.
- Integration events and the outbox (ADR-012).
- Rate limiting of the route at the Gateway (a production gate).

## 2. Flow

`CreateTenantHandler`:

1. **Validate** the slug and the display name with the S1 rules. The subject and the client come from the validated token, never from the body. The body accepts `slug` and `displayName` only: an `ownerSubjectId`, `role` or any other member is rejected with `400`.
2. **Hash the request** with `tenant-onboarding/v1` over the slug, display name and subject. It is the length-prefixed SHA-256 encoding of S1 with another version tag; a test pins the S1 provisioning hash bytes unchanged.
3. **In the subject's locked transaction** (section 3):
   1. Look up the caller's key. If it names a tenant of this caller with the same hash, the answer is a replay (`200`). A different hash gives `422 idempotency-key-reused`. If the creator is no longer an active member, the answer is `404 tenant-not-found`.
   2. Count the caller's self-service tenants. At the limit, the answer is `409 tenant-limit-reached`.
   3. Check the slug. If taken, the answer is `409 tenant-slug-taken`.
   4. Stage the tenant (`Origin = SelfService`), the owner membership and the audit record. Then save and commit.
4. **A slug taken by another user at the same instant** fails the commit on `UX_Tenants_Slug`. The handler classifies the request once more from the committed state, which answers `409`. A second conflict also answers `409`, so there is no loop.

The creation is logged (event 3010) only after the commit. The log carries the tenant, subject, client and membership IDs, never the request body.

## 3. Concurrency

**Unit of serialization: the subject.** A new tenant has no row to lock yet, so ADR-021's tenant lock cannot serialize its creation. The decisions that must not race are one user's key and count.

`TenantOnboardingGate`, per attempt:

| Step | What | Why |
|---|---|---|
| 1 | A fresh DI scope and `DbContext`, then `BEGIN TRANSACTION` at `READ COMMITTED` | A retry starts clean |
| 2 | `SET LOCK_TIMEOUT 5000` | Bounded waits on rows |
| 3 | `sp_getapplock @Resource = N'tenant-onboarding:' + hex(SHA-256(sub)), @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000` | Serializes one subject's requests until commit or rollback. The hash keeps any subject value out of the resource name |
| 4 | Key lookup, count, slug check | Each statement starts after the previous holder committed, so none is stale |
| 5 | `SaveChanges`, commit | Tenant, profile, owner and audit record together |

**Failure mapping:**

| Failure | Response |
|---|---|
| The lock is not granted in time (`sp_getapplock` returns `-1`, or `-2` or `-999`) | `503` with `Retry-After`; nothing done |
| A deadlock (error 1205, or `sp_getapplock` `-3`) | The whole transaction is retried, at most 3 attempts; then `503` |
| A unique violation | Classified again (section 2, step 4) |
| Any other SQL Server failure | `503` with `Retry-After` |

**Database backstops** (the application does not rely on them):

- `UX_Tenants_Slug`.
- `UX_Tenants_SelfServiceSubjectId_ProvisioningKey`: unique, filtered on `[Origin] = N'SelfService'`. It also serves the count.
- `CK_Tenants_Origin_SelfServiceSubject`.

**Evidence** (`OnboardingConcurrencyTests`, real SQL Server 2022). The 8 requests of each race are released at the same moment, after their tokens are created one by one:

- **8 requests, one user, one key:** one `201`, seven `200`, one tenant and one audit record.
- **8 requests, one user, 8 keys, limit 1:** one `201`, seven `409 tenant-limit-reached`, one tenant and one membership.
- **8 users, one slug:** one `201`, seven `409 tenant-slug-taken`.
- **8 users, 8 slugs:** eight `201`.
- **A held lock** (another session holds the subject's application lock):
  - Another user's request completes at once.
  - The blocked user's request answers `503` with `Retry-After` after the lock timeout, and creates nothing.
  - After the release, the retry with the same key succeeds.
- **Mutation run (not committed).** With the `sp_getapplock` call replaced by a constant, 2 of these 5 tests failed in each of 3 runs: the limit race and the held-lock test. The same-key race still passed, because the per-subject unique index and the re-classification back it up.

## 4. Security and authorization

- **Callers:** the existing member policy: `ec_ptype=user`, audience `ec-merchant`, a `sub`, and a non-`svc-` client.
  - The endpoint also requires a `client_id`, which it records for audit; RFC 9068 makes it mandatory in access tokens, and Identity's tokens carry it ([IDENTITY_API_CONTRACT.md section 3](../api/IDENTITY_API_CONTRACT.md#3-jwt-access-token-claims-accepted)). A token without one gets `403`.
  - Service tokens, other front ends' audiences, wrong `typ` and unknown keys get `401` or `403`, as for every member endpoint. All are tested.
- **The owner is the token's subject.**
  - Forged `role`, `tenant_id` or owner claims in the token change nothing.
  - Owner fields in the body are refused (`400`).
  - Through the BFF, a forged `Authorization` header and forged role and owner headers are replaced or ignored.
- **The owner rule (ADR-022, amending ADR-021 section 4).** An owner membership is created only together with its new tenant, by provisioning or by self-service onboarding. No API makes anyone an owner of an existing tenant.
- **Keys cannot cross users.**
  - Two users with the same key get two tenants.
  - A user who sends another user's body and key is answered from their own namespace: `409 tenant-slug-taken`, or `422` once their key names their own tenant. Nobody else's tenant is ever returned.
- **Visibility:** a replay returns the tenant only while the creator is an active member.
- **Email verification:** sign-in requires a confirmed email (D12, PHASE_2_2 section 5.1.4), so no Identity call is made.
- **Logging:** subjects are logged as structured properties, never as metric labels. The display name and the body are never logged, and a test checks the captured events.

## 5. Database schema and migration

Migration `SelfServiceOnboarding`:

- **`Tenants.Origin`** `nvarchar(16) NOT NULL`, default `'Provisioned'` (fills the existing S1 and S2 rows), with `CK_Tenants_Origin` (`Provisioned`, `SelfService`).
- **`Tenants.SelfServiceSubjectId`** `nvarchar(128) NULL`, with `CK_Tenants_Origin_SelfServiceSubject`: required for `SelfService`, absent for `Provisioned`.
- **`UX_Tenants_ProvisionedByClientId_ProvisioningKey`** is now filtered on `[Origin] = N'Provisioned'`.
- **New** `UX_Tenants_SelfServiceSubjectId_ProvisioningKey`, filtered on `[Origin] = N'SelfService'`.
- **`CK_MembershipAuditRecords_Action`** allows `SelfServiceOwnerMembershipCreated`.
- **For a self-service tenant**, the S1 columns record:
  - `ProvisionedByClientId`: the merchant BFF's `client_id`
  - `ProvisioningKey`: the user's key
  - `ProvisioningRequestHash`: the onboarding hash
  - `ProvisionedByActor`: the subject
- **Rollback:** `Down` restores the S2 schema. It **refuses** with error 50003 while self-service tenants exist: it would lose their origin, could break the old per-client key index, and would meet audit records that the append-only trigger does not let it delete.
- **`Origin` is `nvarchar`, like every other enum column.** SQL Server refuses a filtered index that compares a `varchar` column with an `N'…'` constant. The first migration draft used `varchar(16)`, and the SQL Server tests caught it.

**Verified on SQL Server 2022 (Compose) and LocalDB** (`MigrationTests`):

- No pending model changes.
- The exact inventory of indexes (with filters) and check constraints.
- `Down` to S2, to S1 and to empty, then `Up` again.
- An S2 tenant written before the migration reads as `Provisioned` afterwards.
- `Down` refuses while a self-service tenant exists, and leaves the schema unchanged.

## 6. Gateway and BFF

- **Gateway:**
  - No change. The Development route `/api/v1/tenants/{**catch-all}` has no method restriction, and `POST /api/v1/tenants` is forwarded.
  - The dot-segment forms `/api/v1/tenants/../../../internal/v1/tenants`, plain and percent-encoded, are refused (`InternalPathExposureTests`).
  - The form `..%2finternal` reaches the backend re-encoded, as one inert segment under `/api`.
- **BFF:** no change. `/api/**` forwarding passes `Idempotency-Key` and replaces the browser's `Authorization`. CSRF and Fetch Metadata protection applies.
- **End-to-end evidence** (`MerchantOnboardingFlowTests`): a browser, the merchant BFF on the real session Redis, the real Gateway, the real Tenant & Merchant host, and the real Identity issuing the tokens.
  - Without the antiforgery token nothing is forwarded or created.
  - With it, the user's tenant is created: `201`, and the user is `MerchantOwner`.
  - A retry gives `200`, and a second tenant gives `409 tenant-limit-reached`.
  - The tenant list shows it, and the owner can invite staff.
  - No response body contains a JWT.
  - An owner field in the body gives `400`. A forged bearer token and forged headers still create the tenant for the session's user.
  - Without a session, nothing is forwarded.

## 7. Audit

| Column | Content |
|---|---|
| `Action` | `SelfServiceOwnerMembershipCreated` |
| `ActorKind`, `ActorId` | `User`, the subject |
| `MembershipId`, `SubjectId` | The new owner membership and its subject |
| `NewRole`, `NewStatus` | `MerchantOwner`, `Active` |
| `CorrelationId` | The request's `X-Correlation-ID` |

The record commits with the tenant. A test injects a check constraint that rejects it: the request answers `503`, and no tenant, profile or membership remains. After the constraint is removed, a retry with the same key succeeds.

## 8. Test evidence

**Local run of 2026-11-21:**

- **Infrastructure:** SQL Server 2022 (Compose `sqlserver` profile) and the session Redis (Compose `bff` profile).
- **Settings:** `EC_REQUIRE_INFRA_TESTS=true`, `EC_TEST_BROWSERS=true` and `EC_TEST_REDIS_RESTART_CONTAINER` set.

The results are in [PHASE_3_TENANT_MERCHANT.md section 8](PHASE_3_TENANT_MERCHANT.md#s3-self-service-merchant-onboarding).

## 9. Residual risks and production gates

**Residual risks (accepted by the owner's decisions):**

- **No review.** Any confirmed account can create one tenant at once, and so reserve a slug. The limit is per account, not per person.
- **One subject's requests are serialized.** A held lock fails the request with `503` after 5 seconds. This is not load-tested beyond the local 8-way tests.
- **The limit counts creations.** No tenant can be closed yet; closure will decide whether a closed tenant still counts.

**Production gates (open):**

- Rate limiting of `POST /api/v1/tenants` at the Gateway, and the deployed route and host names.
- Merchant applications and review, if the platform requires vetting before a merchant sells (Admin phase).
- Metrics and alerts for `tenant-limit-reached` and onboarding lock timeouts (event IDs 3014 and 3242).
- The first integration event (`TenantProvisioned` or its successor) for both origins, with the outbox (ADR-012).
