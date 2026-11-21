# ADR-022: Self-Service Tenant Onboarding

- **Status:** Accepted
- **Date:** 2026-11-21
- **Phase:** 3 (Tenant & Merchant), slice S3
- **Amends:** [ADR-021](ADR-021-merchant-invitations-and-membership-administration.md) section 4, the sentence "It is granted only by trusted provisioning" (see Decision 1).
- **Implements:** the accepted self-service merchant onboarding of [PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.1.2](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#512-merchant-user-registration-and-onboarding), an MVP item ("merchant onboarding", PROJECT_DECISIONS section 11).
- **Related:**
  - [ADR-014](ADR-014-logical-tenant-isolation.md) (tenancy)
  - [ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md) (BFF)
  - [ADR-017](ADR-017-token-session-and-protocol-strategy.md) (tokens, transactions)
  - [ADR-020](ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md) (one merchant per tenant)
  - [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md) (outbox, deferred)
- **Design and evidence:** [PHASE_3_S3_SELF_SERVICE_ONBOARDING.md](../architecture/PHASE_3_S3_SELF_SERVICE_ONBOARDING.md).

## Context

PHASE_2_2 section 5.1.2 accepted the direction: a person registers a normal account, signs in to the merchant portal, and starts onboarding in Tenant & Merchant, which "creates the merchant and records the owner membership for the authenticated `sub`". S1 built tenant provisioning for trusted service callers only and deferred this path. No document defined Phase 3 S3, and the self-service path left three questions open:

1. **Activation.** PHASE_3_TENANT_MERCHANT.md grouped onboarding "together with merchant applications", which suggests a review step, while 5.1.2 describes immediate creation. No reviewer exists: Admin is not built.
2. **Abuse.** Any registered user could create tenants without limit. Each tenant reserves a platform-unique slug.
3. **Who may create an owner.** ADR-021 section 4 says `MerchantOwner` "is granted only by trusted provisioning". It was written for role changes and invitations of existing tenants, before the self-service path existed.

S1 also left a hazard: its idempotency key is unique per **calling client**. Every merchant-portal user arrives through the same BFF client, so reusing that namespace would let one user's retry replay another user's tenant.

The project owner decided questions 1 and 2 on 2026-11-21, during the S3 scope discovery.

## Decision

### 1. The caller of self-service onboarding becomes the owner of the new tenant

`POST /api/v1/tenants` creates, in one transaction, an `Active` tenant, its merchant profile, a `MerchantOwner` membership for the token's `sub`, and its audit record (`SelfServiceOwnerMembershipCreated`, actor `User`).

ADR-021 section 4 is amended to read:

- `MerchantOwner` is never granted by a role change or an invitation.
- An owner membership is created **only together with its new tenant**: by trusted provisioning, or by self-service onboarding, for the authenticated caller.
- No API makes anyone an owner of an **existing** tenant.

The owner is always the token's subject. The request body cannot name an owner, a role or a membership; unknown members are rejected with `400`.

### 2. Immediate activation; no review step yet (owner decision)

The tenant is `Active` at once, as in 5.1.2. Merchant applications, vetting and review are deferred to the Admin phase and need their own design. Adding them later changes this endpoint's contract (a pending state instead of an active tenant), which is accepted.

### 3. A per-subject limit, enforced under concurrency (owner decision)

- `TenantMerchant:Onboarding:MaxTenantsPerSubject`: 1 by default, 1 to 10. The host refuses to start outside that range.
- It counts the tenants a subject created through self-service. Provisioned tenants and memberships from invitations do not count.
- Every self-service request of one subject runs in one transaction that first takes an exclusive SQL Server application lock on the subject (`sp_getapplock`, owned by the transaction, resource `tenant-onboarding:` + SHA-256 of the subject, 5-second timeout). The key lookup, the count, the slug check and the insert are therefore never stale. Other subjects are not blocked.
- A lock that is not granted answers `503` with `Retry-After`; a deadlock retries the whole transaction, at most 3 attempts.

### 4. Self-service idempotency keys are scoped per subject

- `Idempotency-Key` is required, as for provisioning, and kept on the tenant row for its lifetime.
- The S1 index becomes filtered to provisioned tenants (unique per client). A second filtered unique index makes a self-service key unique per subject.
- A tenant records its origin (`Provisioned` or `SelfService`) and, for self-service, its creating subject; a check constraint ties the two together.
- The request hash uses its own version tag (`tenant-onboarding/v1`), so a provisioning hash and an onboarding hash never match.

### 5. No integration event

As in S1 and S2, ADR-012 applies: no event and no outbox code.

## Consequences

- **Positive:** the MVP's merchant onboarding works without Admin. The owner rule stays strict for existing tenants. The limit holds under parallel requests, and no user can replay or block another user's request through a shared key.
- **Negative:** any confirmed account can create one tenant without review, and so reserve a slug. The limit bounds this per account, not per person. Gateway rate limiting for the route is a production gate.
- **Negative:** the lock serializes one subject's onboarding requests. A held or slow lock fails the request with `503` after 5 seconds.
- **Neutral:** the S3 migration's rollback refuses to run while self-service tenants exist (error 50003). It would otherwise lose their origin and meet audit records that cannot be deleted.

## Alternatives considered

- **An application with approval by a service caller (future Admin).** It matches "merchant applications", but nothing could approve outside tests, and it would ship a flow that cannot complete. Deferred to the Admin phase. Not chosen by the owner.
- **A tenant in a new `PendingReview` status.** It would spread a new status through every authorization check of every endpoint, for a review nobody can perform yet. Rejected.
- **Exactly one self-service tenant, enforced by a filtered unique index on the subject.** Simplest and strongest, but a later change of the limit would need a migration. The owner chose a configurable limit with the lock.
- **No per-subject limit, relying on Gateway rate limiting.** Rate limiting bounds speed, not totals, and is not built for this route. Rejected.
- **Reusing the S1 key index (per client).** It puts all merchant-portal users into one key namespace (the hazard above). Rejected.
- **Serializing with the tenant row lock of ADR-021.** A new tenant has no row to lock before it exists. Rejected for creation; ADR-021's lock still serializes every later change of the tenant.
