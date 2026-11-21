# ADR-021: Merchant Invitations and Membership Administration

- **Status:** Accepted (section 4 owner rule amended by [ADR-022](ADR-022-self-service-tenant-onboarding.md): self-service onboarding also creates the owner of a new tenant)
- **Date:** 2026-11-21
- **Phase:** 3 (Tenant & Merchant), slice S2
- **Resolves:**
  - Decision D14 of [PHASE_2_2_AUTHENTICATION_FLOWS.md](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#113-deferred-decisions): whether invitation acceptance must check that the account's verified email is the invited address.
  - The S1 deferral of [PHASE_3_TENANT_MERCHANT.md section 3](../architecture/PHASE_3_TENANT_MERCHANT.md#3-merchant-roles-permissions-and-membership-rules): enforcing the last-active-owner rule under concurrency.
- **Related:**
  - [ADR-014](ADR-014-logical-tenant-isolation.md) (tenancy)
  - [ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md) (BFF)
  - [ADR-017](ADR-017-token-session-and-protocol-strategy.md) (tokens, service clients, transactions)
  - [ADR-018](ADR-018-account-security-tokens-and-security-email-delivery.md) (single-use secrets, email delivery)
  - [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md) (outbox, deferred)
  - [ADR-020](ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md) (one merchant per tenant)
- **Design and evidence:** [PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md](../architecture/PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md).

## Context

S1 delivered tenant provisioning and the merchant roles `MerchantOwner`, `MerchantAdmin` and `MerchantStaff`, with the membership transitions and the last-owner rule as pure domain logic. S2 adds invitations and membership administration. Four questions decide the design:

1. **Delivering the secret.** An invitation needs a single-use secret that reaches the invitee. Notification does not exist, and no credential may travel in Kafka (PHASE_2_2 F6).
2. **Checking the invitee.** Possession of a forwarded or intercepted link must not be enough to join. Access tokens carry no email (ADR-017 section 2), and Identity had no API that answers for a subject.
3. **The last owner under concurrency.** Two owners who suspend, remove or demote each other at the same moment can both pass `count(active owners) > 1` on the same stale count and leave the tenant without an owner.
4. **Accountability.** Membership changes are security-sensitive: who granted which role, when and why must be reconstructable.

## Decision

### 1. Invitations and their secret

- **The invitation** is its own aggregate with these states:
  - `Pending` changes to `Accepted`, `Cancelled` or `Expired`. All three are terminal.
  - Each invitation belongs to one tenant and carries one non-owner role (`MerchantAdmin` or `MerchantStaff`). The domain and a check constraint both enforce this.
  - A tenant has at most one pending invitation per normalized address (a filtered unique index).
  - Expiry is decided by the server's UTC clock. A pending invitation past its expiry *reads* as expired. The `Expired` transition is recorded the first time a request touches it.
- **The secret** is generated as in ADR-018 section 1:
  - 256 bits from a CSPRNG, base64url-encoded, with the recognizable prefix `ecmi_`.
  - Only its SHA-256 hash is stored, with a unique index.
  - Acceptance looks the invitation up by that hash.
- **Handoff (until Notification exists).** The raw token is returned **once**, in the `201` response that creates the invitation, with `Cache-Control: no-store`. The authorized inviter delivers it.
  - No read or list returns it again, and it is never logged, audited or put in an exception.
  - A lost response is recovered by cancelling and re-inviting.
  - This is not an OAuth token. It reaches the inviter through the merchant BFF like any other response, so BFF token isolation (ADR-016) is unchanged.
  - Delivery through Notification later replaces this handoff without changing the domain rules. Only the token generator and the response field are touched.

### 2. D14: acceptance requires Identity's verified-email verdict

- **Acceptance is tied to the access token's `sub`.** The request body carries only the token. Unknown members are refused, so no subject, tenant, email or role can be supplied. The tenant is always the invitation's own.
- **Identity gains one narrow internal endpoint:** `POST /internal/v1/accounts/{accountId}/email-verification` with `{ "email" }`.
  - It answers `Match`, `Mismatch`, `Unverified` or `Inactive`, or `404` for an unknown account.
  - Tenant & Merchant sends the address it already holds, and **Identity never returns an account's email**.
  - This is an additive integration capability, not a reopening of Phase 2.3.
- **Access to the endpoint:**
  - A service token for `identity-api` with the new scope `svc.identity.account-verification`.
  - `sub == client_id` with the `svc-` prefix.
  - The client must be in the new allow-list `Identity:AccountVerification:AllowedClientIds`, which is empty by default.
- **Tenant & Merchant calls it as `svc-tenant-merchant`:**
  - It authenticates with `private_key_jwt` and caches the token until a minute before expiry.
  - It renews the token once after a `401`.
  - It is a plain HTTP client of a documented contract, with no reference to Identity's projects and no access to its database.
- **Fail closed:** no verdict means no membership (`503`). The integration is required outside Development.
- **Identity is asked before the tenant lock is taken**, so that no lock waits on a remote call. The invitation is then re-read under the lock.

### 3. Tenant-scoped write serialization

- **Every membership and invitation change runs in one short `READ COMMITTED` transaction.** Its first statement takes an update lock on the tenant row, held to the end:
  `SELECT 1 FROM Tenants WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE Id = @tenantId`
- **Under that lock the transaction then:**
  - re-reads the caller's own membership, so that a member suspended or demoted a moment ago cannot act
  - reads the target and counts the active owners
  - applies the domain rule
  - stages the audit record
  - commits everything in one `SaveChanges`
- **Why this lock:**
  - Update locks conflict only with each other. Changes of one tenant are therefore serialized, while readers of that tenant and changes of other tenants proceed.
  - Every change path takes the same lock first, so the lock order is the same everywhere.
  - The guarantee does not depend on the isolation level: EF Core creates the database with `READ_COMMITTED_SNAPSHOT` on, and each statement under the lock sees the previous writer's commit.
- **Failures:**
  - A deadlock (error 1205) retries the **whole** transaction in a fresh scope, at most 3 attempts with jittered backoff, as in ADR-017 section 8.
  - A lock wait is bounded to 5 seconds.
  - A lock timeout or exhausted retries answer `503`, and a `rowversion` conflict answers `409`. A failure is never reported as success.
- **Backstops in the database** (the application never relies on them):
  - The trigger `TR_Memberships_RequireActiveOwner` refuses any `UPDATE` or `DELETE` that leaves a tenant without an active owner. It uses `THROW 50001` and a locking read (`READCOMMITTEDLOCK`).
  - The filtered unique indexes cover current memberships and pending invitations.
  - `rowversion` stays on every mutable table.

### 4. The role policy

Least privilege, as pure domain functions:

- **Staff** administer nothing.
- **An admin** invites, cancels the invitations of, suspends, reactivates and removes **staff** only, and changes no roles.
- **An owner** does all of this for every other member, and is the only role that changes roles (between admin and staff, or demoting another owner).
- **Nobody** changes their own membership.
- **`MerchantOwner` is never granted by a role change or an invitation.** It is granted only by trusted provisioning.
- Leaving a tenant and ownership transfer are later, separately designed operations.

### 5. An append-only audit trail in Tenant & Merchant

- **The table `MembershipAuditRecords`** is written in the same `SaveChanges` as the change, so a failed audit write fails the change.
- **What a record holds:** the action, the actor kind and ID, the membership, invitation and subject concerned, the previous and new role and status, the time and the correlation ID.
- **What a record never holds:** an email address, a token, a hash or a credential.
- **Audited actions:**
  - provisioning of the owner
  - invitation created, cancelled, accepted and expired
  - membership suspended, reactivated, removed and role changed
- **No change, no record:** repeating a change that is already in effect writes nothing and is not audited.
- **Append-only:**
  - The application can only insert.
  - The trigger `TR_MembershipAuditRecords_AppendOnly` (`INSTEAD OF UPDATE, DELETE`, `THROW 50002`) refuses everything else.
- **Not decided here:** audit reads and retention are designed with Admin.

## Consequences

**Positive:**

- **Joining needs both the secret and a verified account:**
  - A stolen database row cannot be turned into an invitation link.
  - A leaked link cannot be used by an account verified for another address.
- **The last-owner rule holds under concurrency, with SQL Server evidence:** races of 2 and 3 owners on suspend, remove and demote, repeated 12 rounds each. Other tenants and readers are not blocked.
- **Every security-relevant change is atomic with its audit record.**
- **Ownership is preserved:** Identity keeps account data, and Tenant & Merchant keeps memberships, invitations and their history.

**Negative and accepted:**

- **Invitation delivery is the inviter's job** until Notification exists. A lost creation response means cancelling and re-inviting.
- **A synchronous Identity dependency at acceptance:** acceptance is unavailable while Identity is.
- **Verification is point-in-time.** Identity has no email-change flow today. If one is added, a later change of address does not affect memberships already accepted.
- **Changes of one tenant are serialized.** Their throughput is bounded by short transactions. Not load-tested.
- **Expired invitations are closed lazily.** There is no background sweeper in S2.
- **The two triggers are hand-written SQL in the migration**, kept in step with the model by the migration inventory test.

## Alternatives considered

- **An `email` claim in merchant-portal access tokens:** contradicts ADR-017 section 2 (no personal data in access tokens), and a claim is no proof of current verification. Rejected.
- **Possession of the secret only (the D14 minimum):** a forwarded or intercepted link would let anyone with any account join. Rejected for merchant roles.
- **Identity returns the account's email for a subject:** discloses personal data that Tenant & Merchant does not need. The verdict endpoint is narrower. Rejected.
- **Storing the raw token, or returning it from reads:** that would persist a credential (ADR-018). Rejected.
- **`SERIALIZABLE` isolation on the membership queries alone:** range locks on the count queries deadlock under contention (both read, both write), and every path would have to choose the same isolation correctly. The explicit tenant-row lock is simpler and gives one lock order. Rejected.
- **`sp_getapplock` per tenant:** equivalent serialization, but a lock outside the data model, which is easier to miss on a new path. Rejected.
- **Optimistic concurrency only (`rowversion`):** it detects conflicting writes to the *same* row. Two owners changing *different* rows both commit. Rejected as the primary mechanism, and kept as a backstop.
- **A generic audit framework, or audit through the outbox:** not needed by a single service, and the outbox is deferred (ADR-012). Rejected.
