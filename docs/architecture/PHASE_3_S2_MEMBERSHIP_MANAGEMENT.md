# Phase 3 S2: Merchant Membership Management

Slice S2 of the Tenant & Merchant service ([PHASE_3_TENANT_MERCHANT.md](PHASE_3_TENANT_MERCHANT.md)): merchant employee invitations, invitation acceptance, membership administration, the concurrent last-active-owner guarantee, and an audit trail.

- **Decisions:** [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md).
- **API:** [TENANT_MERCHANT_API_CONTRACT.md](../api/TENANT_MERCHANT_API_CONTRACT.md) sections 4 and 5.
- **Identity's new internal endpoint:** [IDENTITY_API_CONTRACT.md section 2.13](../api/IDENTITY_API_CONTRACT.md#213-account-email-verification-internal).
- **Status:** implemented and tested locally (section 9). Production gates are in section 10.

## 1. Scope

**In S2:**

- Invitations: create, list, read, cancel, accept.
- Memberships: read one, suspend, reactivate, remove, change the role.
- The caller's own tenants.
- The tenant-scoped write lock, the database backstops, and the audit trail.
- Identity's email verification endpoint and Tenant & Merchant's client for it.
- The Gateway's Development route for acceptance.

**Not in S2:**

- **Invitation email delivery.** Notification does not exist (section 4).
- **A merchant-portal user interface.** No front-end application exists yet. The API is reached through the merchant BFF's `/api/**` forwarding, which needs no change, and is tested end to end there. The invitation landing page (open the link, sign in, accept) belongs to the merchant portal.
- **Leaving a tenant, and ownership transfer.** These are later, separately designed operations.
- **Audit reads and retention.** These come with Admin.
- **Integration events.** ADR-012 defers them.

## 2. Invitation lifecycle

| From | Event | To | Recorded |
|---|---|---|---|
| — | An owner or admin invites (`POST .../invitations`) | `Pending` | `InvitationCreated` |
| `Pending` | The invitee accepts with the token, and Identity's verdict is `Match` | `Accepted` and a new `Active` membership | `InvitationAccepted` |
| `Pending` | An owner or admin cancels | `Cancelled` | `InvitationCancelled` |
| `Pending`, past `ExpiresAt` | The invitation is next presented, cancelled, or re-invited | `Expired` | `InvitationExpired` (actor `System`) |
| `Accepted`, `Cancelled`, `Expired` | anything | unchanged (terminal) | — |

**Rules:**

- **Lifetime:** `TenantMerchant:Invitations:Lifetime`, 7 days by default, allowed from 1 hour to 30 days.
  - The instant `ExpiresAt` itself counts as expired.
  - Reads report the **effective** status: a lapsed pending invitation reads as `Expired`.
- **One pending invitation per address and tenant.**
  - The address is compared after normalization (trimmed, upper invariant), the same as Identity's.
  - A second invitation answers `409 invitation-already-pending` with the existing ID.
  - **Re-inviting** after cancellation, expiry or acceptance creates a new invitation with a new token. A lapsed pending invitation is closed in the same transaction first.
- **At most `MaxOpenPerTenant`** (default 50) open invitations per tenant (`409 invitation-limit-reached`).
- **Acceptance never reactivates or re-roles an existing membership.**
  - A subject with a current (`Active` or `Suspended`) membership of the tenant gets `409 already-a-member`.
  - A **removed** member needs a new invitation, which creates a **new** membership. The removed one stays as history.
- **Acceptance never creates an Identity account or touches credentials.** An invitee without an account registers at Identity first, through the normal flow (PHASE_2_2 section 5.1.2).

## 3. Role and permission matrix

Defined once, in the Domain:

- `MerchantRolePermissions`: the permission per role.
- `MembershipAdministrationPolicy`: the rule per actor and target.

| Operation | Owner | Admin | Staff |
|---|---|---|---|
| View the tenant, list my tenants | ✓ | ✓ | ✓ |
| List and read memberships | ✓ | ✓ | ✗ `403` |
| List and read invitations | ✓ | ✓ | ✗ `403` |
| Invite staff | ✓ | ✓ | ✗ `403` |
| Invite an admin | ✓ | ✗ `403` | ✗ `403` |
| Invite an owner | ✗ `422 owner-role-reserved` | ✗ `422 owner-role-reserved` | ✗ `403` |
| Cancel an invitation | any | staff invitations only | ✗ `403` |
| Suspend, reactivate, remove an owner | ✓ (another owner) | ✗ `403` | ✗ `403` |
| Suspend, reactivate, remove an admin | ✓ | ✗ `403` | ✗ `403` |
| Suspend, reactivate, remove staff | ✓ | ✓ | ✗ `403` |
| Change a role (admin ⇄ staff, or demote an owner) | ✓ | ✗ `403` | ✗ `403` |
| Grant `MerchantOwner` | ✗ `422 owner-role-reserved` (provisioning only) | ✗ | ✗ |
| Change one's own membership | ✗ `403 self-modification-forbidden` | ✗ | ✗ |

**Order of checks:**

1. **Membership (`404`).** The caller must be an active member: a non-member, a suspended or a removed member gets `404 tenant-not-found`, as in S1.
2. **Permission (`403`).** The caller's role must hold the permission.
3. **Target (`404`).** It must belong to this tenant: `membership-not-found` or `invitation-not-found`. A resource of another tenant is never disclosed.
4. **Actor and target pair (`403`).** Self-modification, or the role rule for this target.
5. **The domain rule (`409` or `422`).** The transition (`invalid-transition`), the last-owner rule (`last-active-owner`) and the reserved role (`owner-role-reserved`).

**Why an admin inviting an owner gets `422`, not `403`:** an admin may invite, and the owner role is reserved *whoever* asks.

## 4. Invitation delivery boundary

- **Generation:** the token is 256 bits from a CSPRNG, base64url, with the prefix `ecmi_` (48 characters). Only its SHA-256 hash is stored, in `MerchantInvitations.TokenHash` (`binary(32)`, unique).
- **One response only.** The raw token is returned once, as `acceptanceToken` in the `201` of invitation creation, with `Cache-Control: no-store` and `Pragma: no-cache`.
  - The BFF passes the response through: tested, `Cache-Control: no-store` arrives at the browser.
  - The token is never returned by a read or list, and never logged or audited. Tests scan every string column of every table and every captured log event for it.
  - The response records of the command, the request DTO and the command itself render without the token.
- **Delivery is currently the authorized inviter's responsibility**, for example sharing a merchant-portal link that carries the token.
  - **Why not an email:** inventing a sender or claiming that an email was sent would be false.
  - **Later, Notification replaces this** through the ADR-018 E2 or E3 handoff. The domain rules do not change; only the token generator's caller and the creation response do.
- **Lost response:** cancel and re-invite. A raw token can never be shown again.
- **Acceptance sends the token in the request body**, never in a URL, so that it does not reach access logs or `Referer` headers.

## 5. Identity verification (D14)

**Flow** (`AcceptInvitationHandler`):

1. Validate the token format. Look up the hash. An unknown, altered or malformed token answers `404 invitation-not-found`, and all of them look the same.
2. **Accepted by this caller:** answer `200` with the membership (a replay). No Identity call is made.
   **Accepted by someone else, or cancelled:** `409 invitation-not-pending`.
   **Expired:** record the expiry and answer `410 invitation-expired`.
3. **Ask Identity** `POST /internal/v1/accounts/{sub}/email-verification` with the invited address.
   - `Mismatch` answers `403 invitation-email-mismatch`.
   - `Unverified` answers `403 invitation-email-unverified`.
   - `Inactive` and an unknown account (`404`) answer `403 invitation-account-ineligible`.
   - Nothing changes, and the invitation stays pending for the right person.
4. **Under the tenant lock** (section 6):
   - re-read the invitation (another acceptance may have won)
   - check for a current membership
   - create the membership with the invitation's tenant and role
   - mark the invitation accepted
   - add the audit record
   - commit

**The client** (`IdentityAccountVerifier`, `IdentityServiceTokenProvider`):

- **Configuration:** `TenantMerchant:IdentityVerification`:
  - `BaseAddress` and an optional `TokenEndpoint` (default `connect/token`)
  - `Issuer`: the client assertion's audience, defaulting to `TenantMerchant:Authentication:Issuer`
  - `ClientId` (default `svc-tenant-merchant`), `PrivateKeyPath`, an optional `KeyId`
  - `Timeout` (5 seconds)
- **Startup checks:**
  - Required outside Development, with https.
  - A partial configuration stops the host.
  - In Development without configuration, acceptance answers `503`.
- **Client credentials:**
  - `private_key_jwt` (`client-authentication+jwt`, RS256, a 2-minute assertion).
  - The token is cached until 60 seconds before it expires, and renewed once after a `401`.
- **Fail closed:** anything that is not one of the four answers or a `404` raises `IdentityUnavailableException`, which answers `503 identity-unavailable` with `Retry-After`. That includes another status, an unknown answer, invalid JSON, a timeout, a connection failure or a failed token request.
- **Logging:** the subject and the verdict are logged; the address, the body and the token never are.

**Identity side:**

- The endpoint is `POST /internal/v1/accounts/{accountId}/email-verification`, behind the `account-verification` policy and allow-list. Its contract is in IDENTITY_API_CONTRACT.md section 2.13.
- **The decision order** is fixed: unknown, then inactive, then unconfirmed, then the address.
- **The comparison** uses `AccountEmail.TryNormalize` on both sides. Identity never returns the account's address.
- **Registering the caller** needs configuration only. It is in [IDENTITY_OPERATIONS.md section 1.3](../security/IDENTITY_OPERATIONS.md#13-email-verification-for-tenant--merchant), and `deploy/dev/bff-dev-keys.cs` generates it for local development.

## 6. Concurrency and the last-active-owner guarantee

**The invariant:** every provisioned tenant always has at least one `Active` `MerchantOwner` membership.

**Mechanism:** `TenantWriteGate`, implementing `ITenantWriteGate`.

| Step | What | Why |
|---|---|---|
| 1 | A fresh DI scope and `DbContext`, then `BEGIN TRANSACTION` at `READ COMMITTED` | A retry starts clean |
| 2 | `SET LOCK_TIMEOUT 5000` | A lock wait is bounded and fails as `503` |
| 3 | `SELECT 1 FROM Tenants WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = @tenantId` | Serializes the writers of one tenant; `U` locks are compatible with readers' `S` locks |
| 4 | Re-read the caller's membership (tracked); authorize | A member suspended or demoted a moment ago cannot act |
| 5 | Read the target; count the active owners | Done after the previous writer committed, so never stale |
| 6 | Apply the domain change; stage the audit record | The change and its audit row are one unit |
| 7 | `SaveChanges`, then commit (or roll back) | Atomic |

- **All mutation paths go through the gate:** invitation creation, cancellation and acceptance (including recording an expiry), and suspension, reactivation, removal and role change.
- **Provisioning creates a new tenant** and needs no lock.
- **Retry:** a deadlock (error 1205) retries the whole transaction, at most 3 attempts with jittered exponential backoff.
- **Failure mapping:**

  | Failure | Response |
  |---|---|
  | Retries exhausted, lock timeout (error 1222), or another SQL failure | `503` with `Retry-After`; nothing done |
  | `DbUpdateConcurrencyException` | `409 concurrency-conflict` |
  | Unique violation | `409` (`already-a-member` or `invitation-already-pending`) |
  | Error 50001 from the trigger | `409 last-active-owner` |

- **Isolation level:** EF Core creates the database with `READ_COMMITTED_SNAPSHOT` on. The migration test records this.
  - The gate does not depend on it: `UPDLOCK` serializes writers either way, and each statement under the lock starts after the previous writer's commit.
  - The trigger forces a locking read (`READCOMMITTEDLOCK`) so that it cannot judge from a row version.

**Database backstops** (the application never relies on them):

- `TR_Memberships_RequireActiveOwner`, `AFTER UPDATE, DELETE`: if a changed row was an active owner and its tenant has no active owner left, `THROW 50001`. The filtered index `IX_Memberships_TenantId_ActiveOwner` serves it and the count.
- `UX_Memberships_TenantId_SubjectId_Current` (from S1), `UX_MerchantInvitations_TenantId_NormalizedEmail_Pending` and `UX_MerchantInvitations_TokenHash`.
- `rowversion` on every mutable table.
- Check constraints that tie each invitation status to its timestamps and actors, and keep `MerchantOwner` out of invitations.

**Why the rule rarely fires as a domain outcome:** an owner can only act on *another* owner. So when the target is an active owner, there are at least two active owners, unless the count is stale. The race is exactly where the old count would be wrong, and there the second writer, re-reading under the lock, finds that it is no longer an active owner itself. It then answers `404`, or `403` when it was demoted.

**Evidence** (`OwnerConcurrencyTests`, real SQL Server 2022):

- **Two owners suspend, remove or demote each other at the same instant** (a barrier), 12 rounds per action:
  - Exactly one `200`, and the other `404` (`403` for a demotion).
  - Exactly one active owner afterwards, and exactly one audit record.
- **Three owners each remove or demote the two others** (6 simultaneous requests), 12 rounds:
  - Always exactly one active owner.
  - Exactly two audited ownership changes.
  - Only `200`, `403`, `404` or `409` answers.
- **With another transaction holding the tenant lock:**
  - A read of the same tenant and a change of another tenant complete at once.
  - The change of the locked tenant waits and completes after the release.
  - A lock that is never released yields `503` after the lock timeout, with nothing changed.
- **12 tenants changed in parallel** all succeed.
- **The test itself was checked (mutation run):** with the `UPDLOCK` hint removed, 5 of these 7 tests failed: the three two-owner races, the lock-serialization test and the lock-timeout test.
- **The trigger on its own:** a direct SQL `UPDATE` or `DELETE` that would leave no active owner fails with 50001.

## 7. Audit

`MembershipAuditRecords` (ADR-021 section 5):

| Column | Content |
|---|---|
| `Id`, `TenantId`, `OccurredAt` | Identity, tenant (FK) and UTC time |
| `Action` | `OwnerMembershipProvisioned`, `InvitationCreated`, `InvitationCancelled`, `InvitationAccepted`, `InvitationExpired`, `MembershipSuspended`, `MembershipReactivated`, `MembershipRemoved`, `MembershipRoleChanged` (check constraint) |
| `ActorKind`, `ActorId` | `User` (the `sub`), `Service` (the `client_id`, for provisioning) or `System` (`tenant-merchant`, for recorded expiries) |
| `MembershipId`, `InvitationId`, `SubjectId` | What was changed, and whose membership |
| `PreviousRole`, `NewRole`, `PreviousStatus`, `NewStatus` | Non-sensitive before and after state |
| `CorrelationId` | The request's `X-Correlation-ID` (at most 64 characters, otherwise not recorded) |

- **Never recorded:** an email address, the token or its hash, an authorization header, or any credential. A unit test checks the type, and an integration test checks the stored row.
- **Same `SaveChanges` as the change.** A test injects a temporary check constraint that rejects the audit insert: the suspension, or the acceptance, answers `503`, and nothing of the change remains. Without the constraint, the retry succeeds.
- **Append-only.** The application only inserts, and `TR_MembershipAuditRecords_AppendOnly` refuses every `UPDATE` and `DELETE` (50002). A future retention job will need a reviewed procedure that bypasses the trigger.
- **No metric labels.** The service has no metrics yet. Subjects and addresses are logged as structured properties only where listed above, never as labels.

## 8. Gateway and BFF

**Gateway:**

- **New Development route** `tenant-merchant-invitations`: host `localhost:7000`, path `/api/v1/merchant-invitations/{**catch-all}`.
- **`GET /api/v1/tenants`** is matched by the existing `/api/v1/tenants/{**catch-all}` route.
- **`/internal/**` stays blocked on every route.** That includes forms through the new prefix (`/api/v1/merchant-invitations/../../../internal/...`, percent-encoded dot segments) and Identity's new `/internal/v1/accounts/{id}/email-verification`.

**BFF:**

- **No code change.** The merchant BFF forwards `/api/**` with the session's `ec-merchant` access token.
- **It strips the cookie and the antiforgery header, and replaces any browser `Authorization` header.**
- **CSRF and Fetch Metadata apply to every state-changing call**, including invitation creation and acceptance.

**End-to-end evidence** (`MerchantMembershipFlowTests`, BFF integration suite):

- **The path:** a browser, the merchant BFF on the real session Redis, the real Gateway with its committed Development routes, and the real Tenant & Merchant host on its own database. The real Identity issues every token, and verifies the invitee's email for a real `svc-tenant-merchant` `private_key_jwt` client.
- **Owner and invitee:**
  - The owner invites. Without the antiforgery token nothing is forwarded.
  - The response carries `Cache-Control: no-store`.
  - The invitee accepts and sees the tenant.
  - No response body contains a JWT.
- **The wrong user:** a user verified for another address gets `403 invitation-email-mismatch`.
- **Forged browser values:**
  - A staff member sends a forged `Authorization` header and forged role and tenant headers to suspend the owner. The answer is `403 permission-denied`: the BFF used the session's token, and the service authorized from its own data.
  - After the owner suspends them, they see no tenant.
- **Internal paths:** the Gateway answers `404` for internal paths, including Identity's new endpoint.

## 9. Database schema and migration

Migration `20261121131342_MerchantMembershipAdministration`:

- **`MerchantInvitations`:**
  - PK `Id`, FK `TenantId`.
  - `Email` and `NormalizedEmail` (`nvarchar(256)`), `Role`, `Status`, `InvitedBySubjectId`.
  - `CreatedAt` and `ExpiresAt`, then the accepted, cancelled and expired columns (times, subjects and the membership).
  - `TokenHash binary(32)`, `RowVersion`.
  - Indexes: `UX_..._TokenHash`, `UX_..._TenantId_NormalizedEmail_Pending` (`[Status]=N'Pending'`), `IX_..._TenantId_CreatedAt`.
  - Six check constraints (role, status, expiry after creation, and the accepted, cancelled and expired consistency).
- **`MembershipAuditRecords`:** as in section 7, with `IX_..._TenantId_OccurredAt` and two check constraints. No `rowversion`.
- **`Memberships`:** new indexes `IX_Memberships_TenantId_ActiveOwner` (filtered) and `IX_Memberships_SubjectId_Status` (the caller's tenants).
- **Triggers:** `TR_Memberships_RequireActiveOwner` and `TR_MembershipAuditRecords_AppendOnly`, both declared with `HasTrigger` so that EF Core does not use `OUTPUT` without `INTO`.

**Verified on SQL Server 2022 (Compose) and LocalDB** (`MigrationTests`):

- No pending model changes.
- The exact inventory of indexes (with filters), check constraints, foreign keys, `rowversion` columns and triggers.
- `Down` to S1 removes every S2 object and leaves the S1 schema, `Down` to empty, and `Up` again.

## 10. Test evidence

**Local run of 2026-11-21:**

- **Infrastructure:** SQL Server 2022 (Compose `sqlserver` profile) and the session Redis (Compose `bff` profile).
- **Settings:** `EC_REQUIRE_INFRA_TESTS=true`, `EC_TEST_BROWSERS=true` (Playwright Chromium, Firefox, WebKit) and `EC_TEST_REDIS_RESTART_CONTAINER` set. Nothing was skipped.
- **Build:** `dotnet build EnterpriseCommerce.sln -c Release`: 0 warnings, 0 errors.
- **Tests:** `dotnet test --solution EnterpriseCommerce.sln -c Release`: **1010 tests, 1010 passed, 0 failed, 0 skipped**. S1 ended at 756.

| Project | Tests | New or changed in S2 |
|---|---|---|
| TenantMerchant.UnitTests | 236 | Invitation lifecycle and expiry boundary, email normalization, the complete role matrix, token format and hash, audit contents, invitation settings, the Identity client (answers, fail-closed cases, token cache and renewal, options) |
| TenantMerchant.IntegrationTests (SQL Server) | 115 | Invitations, acceptance with every Identity verdict, Identity unavailable, replay and reuse, forged input and claims, concurrent creation and acceptance, administration matrix, self-modification, lifecycle, tenant scoping, audit content, audit rollback, triggers, caller's tenants, owner concurrency, the migration inventory and down-and-up |
| Identity.UnitTests | 169 | The verification decision order |
| Identity.IntegrationTests | 166 | The verification endpoint: policy matrix, answers, validation, `503`, no address in the logs |
| Bff.IntegrationTests | 60 | `MerchantMembershipFlowTests`, the end-to-end flow (section 8) |
| Bff.BrowserTests | 18 | unchanged |
| Bff.UnitTests | 103 | unchanged |
| Gateway.IntegrationTests | 66 | The acceptance route, the new paths, internal-path forms through the new prefix |
| ArchitectureTests | 77 | No messaging package and no outbox or integration-event type in any service (ADR-012) |

**Other checks:**

- **LocalDB:** the TenantMerchant integration suite also ran against LocalDB: 115 passed.
- **Secret scan:** the CI pattern, extended with `ecmi_` tokens, found nothing in the captured test output.
- **Mutation run:** without the tenant lock, 5 of the 7 concurrency tests fail (section 6).

**In CI** the same suite runs on GitHub Actions with its own SQL Server 2022 and Redis services. These results are local; CI has not run on these commits yet, because they are not pushed.

## 11. Residual risks and production gates

**Residual risks (accepted):**

- **Invitation delivery is manual.** The inviter must pass the token on a safe channel. A token seen by someone else is still useless to an account that is not verified for the invited address.
- **Acceptance depends on Identity.** It is unavailable (`503`) while Identity is.
- **The email check is point-in-time.** Identity has no email-change flow yet. If one is added, accepted memberships are not re-checked.
- **Access tokens stay valid until they expire.** A suspended member's token keeps working for up to 10 minutes, but every request re-reads the membership, so the member sees nothing (S1).
- **Changes of one tenant are serialized.** Not load-tested beyond the local tests.
- **Expired invitations are closed lazily.** They read as expired, but stay `Pending` in storage until touched.

**Production gates (open):**

- Notification-based invitation delivery (ADR-018 E2 or E3), and the merchant-portal landing page.
- Deployed Identity and Gateway addresses, https for `TenantMerchant:IdentityVerification`, and the `svc-tenant-merchant` key in a protected store, with rotation.
- Retention periods for closed invitations (they hold an email address) and for audit records, and the procedure that bypasses the append-only trigger.
- Audit reads for Admin. Metrics and alerts for `last-active-owner`, `identity-unavailable` and lock timeouts.
- Load and soak tests of per-tenant serialization.
- Rate limiting of invitation creation and acceptance at the Gateway. The token's 256 bits make guessing infeasible, but creation could be abused by a compromised member account.
