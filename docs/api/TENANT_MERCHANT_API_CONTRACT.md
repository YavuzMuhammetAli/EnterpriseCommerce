# Tenant & Merchant API Contract

- **Status:** Accepted and implemented for Phase 3 slices S1 (provisioning, member reads) and S2 (memberships, invitations). Later slices extend it. An endpoint exists only once its slice is merged.
- **Design:** [PHASE_3_TENANT_MERCHANT.md](../architecture/PHASE_3_TENANT_MERCHANT.md) and, for S2, [PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md](../architecture/PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md). Decisions: [ADR-020](../decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md) (naming, the tenant-merchant model) and [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md) (invitations, membership administration).
- **Conventions:** the cross-service conventions of [IDENTITY_API_CONTRACT.md section 1](IDENTITY_API_CONTRACT.md#1-conventions-baseline) apply: `/api/v1`, lowercase kebab-case paths, camelCase JSON, `X-Correlation-ID`, `Idempotency-Key`, and RFC 9457 problem details. Pagination follows [README.md](README.md#conventions).

## 1. Summary

| Endpoint | Caller | Section |
|---|---|---|
| `POST /internal/v1/tenants` | Allow-listed service client (scope `svc.tenant-merchant.provisioning`) | [3.1](#31-provision-a-tenant) |
| `GET /internal/v1/tenants/{tenantId}` | Allow-listed service client (same scope) | [3.2](#32-read-a-tenant-internal) |
| `GET /api/v1/tenants/{tenantId}` | Merchant-portal user who is an active member | [4.1](#41-read-a-tenant-member) |
| `GET /api/v1/tenants/{tenantId}/memberships` | Merchant-portal user with `MerchantOwner` or `MerchantAdmin` | [4.2](#42-list-memberships) |
| `GET /api/v1/tenants` | Any merchant-portal user (their own tenants) | [4.3](#43-list-my-tenants) |
| `GET /api/v1/tenants/{tenantId}/memberships/{membershipId}` | `MerchantOwner`, `MerchantAdmin` | [4.4](#44-read-a-membership) |
| `POST /api/v1/tenants/{tenantId}/memberships/{membershipId}/suspend`, `/reactivate`, `/remove` | `MerchantOwner`; `MerchantAdmin` for staff | [4.5](#45-suspend-reactivate-or-remove-a-membership) |
| `PUT /api/v1/tenants/{tenantId}/memberships/{membershipId}/role` | `MerchantOwner` | [4.6](#46-change-a-memberships-role) |
| `POST /api/v1/tenants/{tenantId}/invitations` | `MerchantOwner`; `MerchantAdmin` for staff | [5.1](#51-create-an-invitation) |
| `GET /api/v1/tenants/{tenantId}/invitations`, `GET .../invitations/{invitationId}` | `MerchantOwner`, `MerchantAdmin` | [5.2](#52-list-invitations), [5.3](#53-read-an-invitation) |
| `POST /api/v1/tenants/{tenantId}/invitations/{invitationId}/cancel` | `MerchantOwner`; `MerchantAdmin` for staff invitations | [5.4](#54-cancel-an-invitation) |
| `POST /api/v1/merchant-invitations/accept` | Any merchant-portal user holding the token, whose verified email is the invited address | [5.5](#55-accept-an-invitation) |
| `GET /health`, `/health/live`, `/health/ready` | Anonymous | [8](#8-health) |

There is no endpoint that grants `MerchantOwner` except provisioning. Invitations and role changes grant only `MerchantAdmin` and `MerchantStaff`.

## 2. Authentication

Every endpoint except health requires `Authorization: Bearer <access token>` issued by Identity. The service validates it locally ([ADR-017 section 4](../decisions/ADR-017-token-session-and-protocol-strategy.md)):

- signature against Identity's published JWKS, fetched from `TenantMerchant:Authentication:MetadataAddress` (default: the issuer's `.well-known/openid-configuration`)
- the exact issuer `TenantMerchant:Authentication:Issuer`
- `RS256` only and `typ: at+jwt`
- `exp` and `nbf` with 30 seconds of clock skew
- an audience of `tenant-merchant-api` (`ServiceAudience`) or `ec-merchant` (`UserAudiences`)

| Result | When |
|---|---|
| `401` | No token, or one that fails validation: bad signature, another issuer, expired, another `typ` or algorithm, or another service's or front end's audience |
| `403` | A valid token of the wrong kind of caller: a user token on an internal endpoint, a service token on a member endpoint, a missing scope, or a client that is not allow-listed |

Tokens carry no roles and no tenant ([ADR-017 section 2](../decisions/ADR-017-token-session-and-protocol-strategy.md)). A forged `role`, `roles`, `tenant_id` or `tenant_context` claim is ignored. Merchant roles come from this service's data only.

## 3. Internal API (service callers)

The Gateway never routes `/internal/**`. These endpoints are reachable on the internal network only.

A service token is accepted only if all of these hold:

- `ec_ptype=service`
- the audience `tenant-merchant-api`
- the scope `svc.tenant-merchant.provisioning` (`scope` is space-separated)
- `sub` equal to `client_id`, with the `svc-` prefix
- the `client_id` is listed in `TenantMerchant:Provisioning:AllowedClientIds`

The list is **empty by default**, which denies every caller. The service refuses to start if an entry lacks the `svc-` prefix.

**Intended caller:** Admin, for SuperAdmin tenant management, as `svc-admin`. Admin does not exist yet, so no client is allow-listed outside the tests. Admin authorizes its platform administrator; this service authorizes the calling client, never an administrator role.

**Identity configuration for a caller** (configuration only, no Identity code change; example values):

```
Identity__Clients__N__ClientId=svc-admin
Identity__Clients__N__Kind=Service
Identity__Clients__N__ServiceScopes__0__Scope=svc.tenant-merchant.provisioning
Identity__Clients__N__ServiceScopes__0__Audience=tenant-merchant-api
```

Service tokens live 5 minutes by default.

### 3.1 Provision a tenant

`POST /internal/v1/tenants`

Creates, in **one** transaction, the tenant, its merchant profile, the initial `MerchantOwner` membership of an existing Identity subject, and the audit record of that membership (`OwnerMembershipProvisioned`, actor: the calling client, S2).

| Header | Rule |
|---|---|
| `Idempotency-Key` | **Required.** 1 to 100 characters of letters, digits, `.`, `_`, `:` or `-` (a UUID is recommended). It is unique per calling client and kept for the life of the tenant. |

**Body:**

| Field | Rule |
|---|---|
| `slug` | 3 to 63 characters of `a-z`, `0-9` and single `-`, starting and ending with a letter or digit. Unique across the platform and immutable. Never normalized: uppercase or padding is rejected. |
| `displayName` | The merchant's display name: 1 to 200 characters after trimming, no control characters |
| `ownerSubjectId` | The Identity `sub` of the initial owner: 1 to 128 characters, no whitespace |
| `actorReference` | The caller's opaque reference to the person who asked: 1 to 128 characters of letters, digits, `.`, `_`, `:` or `-`. Recorded for audit only; it never authorizes anything. |

Any other member (for example `role`, `memberships`, `status` or `id`) is rejected with `400`. The owner role is fixed by the server.

**Responses:**

| Code | Meaning |
|---|---|
| `201` | Created. `Location: /internal/v1/tenants/{id}`. The body is a provisioned tenant (below). |
| `200` | A retry: the same client already used this key for this request (slug, display name, owner and actor, as validated). The original tenant is returned and nothing new is created. |
| `400` | Validation problem with `errors` per field (including `Idempotency-Key`), or a malformed body |
| `401`, `403` | Section 2 |
| `409` | `tenant-slug-taken`: another tenant has the slug |
| `422` | `idempotency-key-reused`: this client used the key before for a different request |
| `503` | The database could not complete the request. Nothing was created; retry with the same key. `Retry-After` is set. |

**Provisioned tenant:**

```json
{
  "id": "01a1fde9-71bd-71d3-94cb-d4db996b5f63",
  "slug": "acme",
  "status": "Active",
  "createdAt": "2026-11-21T11:41:37.0853974+00:00",
  "merchantProfile": { "displayName": "Acme Store" },
  "ownerMembership": {
    "id": "01a1fde9-71bd-79f1-b8c9-4ab77515e9d3",
    "subjectId": "0f8fad5b-d9cb-469f-a165-70867728950e",
    "role": "MerchantOwner",
    "status": "Active",
    "createdAt": "2026-11-21T11:41:37.0853974+00:00",
    "statusChangedAt": "2026-11-21T11:41:37.0853974+00:00"
  }
}
```

`ownerMembership` is the tenant's initial owner membership in its current state.

**Not verified:** provisioning does not check that `ownerSubjectId` exists in Identity. Identity's email verification API (S2, section 6) answers a different question and is used only for invitation acceptance. The allow-listed caller is responsible for passing a real subject.

### 3.2 Read a tenant (internal)

`GET /internal/v1/tenants/{tenantId}`, for example to reconcile a provisioning request whose response was lost.

- `200`: a provisioned tenant (section 3.1).
- `404` `tenant-not-found`.
- `401` and `403` as in section 2.

## 4. Member API (merchant-portal users)

A user token is accepted only if all of these hold:

- `ec_ptype=user`
- the audience `ec-merchant`
- a `sub`
- a `client_id` without the `svc-` prefix

The tenant ID comes from the path and is **never trusted by itself**. Every request checks the caller's membership in that tenant, from this service's own data:

| Situation | Response |
|---|---|
| No membership, or the membership is `Suspended` or `Removed`, or the tenant does not exist | `404` `tenant-not-found`: indistinguishable, so tenant IDs cannot be probed |
| An active member whose role lacks the permission | `403` `permission-denied` |
| A membership or invitation ID that is not in this tenant | `404` `membership-not-found` or `invitation-not-found`: a resource of another tenant is never disclosed |

A non-GUID `tenantId`, `membershipId` or `invitationId` answers `404`.

**Role and permission matrix** (S2, [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md); the full table with the order of checks is in [PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md section 3](../architecture/PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md#3-role-and-permission-matrix)):

| Operation | `MerchantOwner` | `MerchantAdmin` | `MerchantStaff` |
|---|---|---|---|
| Read the tenant (4.1), list my tenants (4.3) | ✓ | ✓ | ✓ |
| List and read memberships (4.2, 4.4) and invitations (5.2, 5.3) | ✓ | ✓ | `403` |
| Invite (5.1) | admins and staff | staff | `403` |
| Cancel an invitation (5.4) | any | staff invitations | `403` |
| Suspend, reactivate, remove (4.5) | any other member | staff | `403` |
| Change a role (4.6) | any other member | `403` | `403` |

- **Nobody** changes their own membership: `403` `self-modification-forbidden`.
- **`MerchantOwner` is never granted** through this API: `422` `owner-role-reserved`, for anyone who may otherwise invite or change roles.

**Writes are serialized per tenant.** Every change below runs in one transaction that first locks the tenant row, re-authorizes the caller, applies the change and writes its audit record ([ADR-021 section 3](../decisions/ADR-021-merchant-invitations-and-membership-administration.md#3-tenant-scoped-write-serialization)).

- A lock that cannot be taken within 5 seconds, a deadlock that persists after 3 attempts, or a database failure answer `503` with `Retry-After`. Nothing is changed.
- A rejected concurrent write answers `409` `concurrency-conflict`.

**Idempotency without a key:**

- Repeating a change that is already in effect answers `200` with the current state, and writes and audits nothing.
- Repeating an acceptance by the same caller is a replay (5.5).
- No endpoint of this section needs `Idempotency-Key`.

### 4.1 Read a tenant (member)

`GET /api/v1/tenants/{tenantId}`: every active member.

`200`:

```json
{
  "id": "…",
  "slug": "acme",
  "status": "Active",
  "createdAt": "…",
  "merchantProfile": { "displayName": "Acme Store" },
  "membership": { "id": "…", "subjectId": "…", "role": "MerchantStaff", "status": "Active", "createdAt": "…", "statusChangedAt": "…" }
}
```

`membership` is the caller's own membership. `statusChangedAt` was added in S2; it is the time of the last status change, or the creation time if there was none.

### 4.2 List memberships

`GET /api/v1/tenants/{tenantId}/memberships?page=1&pageSize=20`: `MerchantOwner` and `MerchantAdmin` only. `MerchantStaff` gets `403`.

- Current memberships only (`Active` and `Suspended`), oldest first.
- `page` is 1-based (default 1). `pageSize` defaults to 20 and is at most 100. Out-of-range or non-integer values get `400`.
- `200`: `{ "items": [membership, …], "page": 1, "pageSize": 20, "totalCount": 24 }`.

### 4.3 List my tenants

`GET /api/v1/tenants?page=1&pageSize=20`: the tenants in which the caller has an **active** membership, oldest membership first.

- No permission applies: the caller reads only their own data.
- Suspended and removed memberships are not listed, because they grant nothing.
- `200`: `{ "items": [tenant as in 4.1, …], "page": 1, "pageSize": 20, "totalCount": 2 }`, each item with the caller's membership.

### 4.4 Read a membership

`GET /api/v1/tenants/{tenantId}/memberships/{membershipId}`: `MerchantOwner` and `MerchantAdmin`.

- `200`: a membership in any status, including `Removed`, which is kept as history.
- `404` `membership-not-found`.

### 4.5 Suspend, reactivate or remove a membership

`POST /api/v1/tenants/{tenantId}/memberships/{membershipId}/suspend`, `.../reactivate` and `.../remove`. There is no body.

**Transitions:**

- `Active` → `Suspended`
- `Suspended` → `Active`
- `Active` or `Suspended` → `Removed`

`Removed` is terminal: a removed subject needs a new invitation, which creates a new membership.

| Code | Meaning |
|---|---|
| `200` | The membership in its new state, or its current state if it was already there |
| `403` | `permission-denied` (see the matrix) or `self-modification-forbidden` |
| `404` | `tenant-not-found` or `membership-not-found` |
| `409` | `invalid-transition` (for example out of `Removed`), `last-active-owner` (the tenant would have no active owner) or `concurrency-conflict` |
| `503` | The change could not be completed. Nothing changed. |

A suspended member is treated as a non-member from their next request on.

### 4.6 Change a membership's role

`PUT /api/v1/tenants/{tenantId}/memberships/{membershipId}/role` with `{ "role": "MerchantAdmin" | "MerchantStaff" }`. Owners only.

- The membership must be `Active`. Otherwise the answer is `409` `invalid-transition`.
- An owner may demote another owner. The last active owner cannot be demoted (`409` `last-active-owner`).
- `"MerchantOwner"` answers `422` `owner-role-reserved`.
- Any other value, a numeric value, a missing role or an unknown member answers `400`.
- Responses are otherwise as in 4.5.

## 5. Invitations

Merchant employee invitations ([ADR-021 section 1](../decisions/ADR-021-merchant-invitations-and-membership-administration.md#1-invitations-and-their-secret)). The lifecycle is in [PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md section 2](../architecture/PHASE_3_S2_MEMBERSHIP_MANAGEMENT.md#2-invitation-lifecycle).

**Invitation** (read model):

```json
{
  "id": "…",
  "tenantId": "…",
  "email": "new.hire@example.test",
  "role": "MerchantStaff",
  "status": "Pending",
  "invitedBySubjectId": "…",
  "createdAt": "…",
  "expiresAt": "…",
  "acceptedAt": null,
  "acceptedBySubjectId": null,
  "acceptedMembershipId": null,
  "cancelledAt": null,
  "cancelledBySubjectId": null,
  "expiredAt": null
}
```

- **`status` is the effective status:** `Pending`, `Accepted`, `Cancelled` or `Expired`. A pending invitation past `expiresAt` reads as `Expired`. `expiredAt` is set once the expiry is recorded.
- **No read ever contains the acceptance token or its hash.**

### 5.1 Create an invitation

`POST /api/v1/tenants/{tenantId}/invitations` with `{ "email": "…", "role": "MerchantAdmin" | "MerchantStaff" }`.

| Field | Rule |
|---|---|
| `email` | A plain address of at most 256 characters with a dotted host (no display name). Trimmed; compared case-insensitively (upper invariant), as Identity does. |
| `role` | `MerchantAdmin` (owners only) or `MerchantStaff`. `MerchantOwner` answers `422`. |

Any other member answers `400`.

**`201` response:**

- `Location: /api/v1/tenants/{tenantId}/invitations/{id}`
- `Cache-Control: no-store` and `Pragma: no-cache`
- the body:

```json
{
  "invitation": { "...": "as above" },
  "acceptanceToken": "ecmi_2Vq…(43 base64url characters)"
}
```

**The acceptance token is returned this once only.**

- It is a single-use credential for this invitation. Only its SHA-256 hash is stored.
- The inviter delivers it to the invitee; there is no email delivery yet (Notification does not exist).
- A lost response is recovered by cancelling and re-inviting.

| Code | Meaning |
|---|---|
| `201` | Created |
| `400` | `validation-failed` (`email`, `role`) or a malformed body |
| `403`, `404` | Section 4 |
| `409` | `invitation-already-pending` (with `invitationId`): the address has a pending invitation in this tenant. `invitation-limit-reached`: the tenant has `TenantMerchant:Invitations:MaxOpenPerTenant` (default 50) open invitations. |
| `422` | `owner-role-reserved` |
| `503` | Nothing was created |

**Lifetime:** `TenantMerchant:Invitations:Lifetime`, 7 days by default, allowed from 1 hour to 30 days. A lapsed pending invitation for the same address is closed (`Expired`) in the same transaction as its replacement.

### 5.2 List invitations

`GET /api/v1/tenants/{tenantId}/invitations?status=Pending&page=1&pageSize=20`: owners and admins.

- Newest first.
- `status` is optional and filters by **effective** status. It must be exactly `Pending`, `Accepted`, `Cancelled` or `Expired`; anything else answers `400`.
- Paging as in 4.2.

### 5.3 Read an invitation

`GET /api/v1/tenants/{tenantId}/invitations/{invitationId}`: owners and admins.

- `200`: an invitation.
- `404`: `invitation-not-found`.

### 5.4 Cancel an invitation

`POST /api/v1/tenants/{tenantId}/invitations/{invitationId}/cancel`. There is no body. Owners cancel any invitation, and admins cancel staff invitations.

| Code | Meaning |
|---|---|
| `200` | Cancelled, or already cancelled (unchanged) |
| `409` | `invalid-transition`: already accepted |
| `410` | `invitation-expired`: it had lapsed, and its expiry is now recorded |
| `403`, `404`, `503` | As above |

### 5.5 Accept an invitation

`POST /api/v1/merchant-invitations/accept` with `{ "token": "ecmi_…" }`: any merchant-portal user. No membership is needed, since the invitee is not a member yet.

- **The token travels in the body only.** Any other member (a tenant, a subject, an email, a role) answers `400`.
- **The membership is created for the access token's `sub`**, in the invitation's tenant, with the invitation's role.
- **Identity must confirm** that the caller's account is active and its **verified** email is the invited address ([IDENTITY_API_CONTRACT.md section 2.13](IDENTITY_API_CONTRACT.md#213-account-email-verification-internal)).
- **Acceptance never creates an Identity account.**

| Code | Meaning |
|---|---|
| `201` | `{ "tenantId": "…", "membership": { … } }` with `Location: /api/v1/tenants/{tenantId}/memberships/{id}` |
| `200` | A replay: this caller already accepted this invitation. The same membership, in its current state. Identity is not asked again. |
| `400` | `validation-failed` (no token) or a malformed body |
| `401` | No valid user token |
| `403` | `invitation-email-mismatch`, `invitation-email-unverified`, `invitation-account-ineligible` (account disabled or unknown to Identity), or a non-user token |
| `404` | `invitation-not-found`: no invitation has this token. An unknown, altered and malformed token all answer the same. |
| `409` | `invitation-not-pending` (cancelled, or accepted by someone else), `already-a-member` (the caller has a current membership of the tenant: acceptance never reactivates or re-roles one) or `concurrency-conflict` |
| `410` | `invitation-expired` |
| `503` | `identity-unavailable` (no verdict from Identity, fail closed) or the database could not complete the change. Nothing changed. `Retry-After` is set. |

**Concurrent acceptances of one invitation create exactly one membership.** The others see the accepted invitation: a replay for the same caller, `409` for anyone else.

## 6. Outbound: Identity email verification

Tenant & Merchant calls Identity's internal email verification endpoint as the service client `svc-tenant-merchant` (`private_key_jwt`, scope `svc.identity.account-verification`, audience `identity-api`).

| Setting (`TenantMerchant:IdentityVerification`) | Meaning |
|---|---|
| `BaseAddress` | Identity's internal address. **Required outside Development**, and https there. |
| `TokenEndpoint` | Default: `connect/token` under `BaseAddress` |
| `Issuer` | The client assertion's audience. Default: `TenantMerchant:Authentication:Issuer`. |
| `ClientId` | Default `svc-tenant-merchant`. It must have the `svc-` prefix. |
| `PrivateKeyPath` | A PEM file with the client's RSA private key, outside the repository |
| `KeyId` | The `kid` in the client's registered JWKS, if any |
| `Timeout` | Default 5 seconds |

- A partial configuration stops the host.
- In Development without any of these settings, acceptance answers `503` `identity-unavailable`.
- Identity's side is configured with `Identity:AccountVerification:AllowedClientIds` ([IDENTITY_OPERATIONS.md section 1.3](../security/IDENTITY_OPERATIONS.md#13-email-verification-for-tenant--merchant)).

## 7. Gateway

- **Development routes:**
  - `tenant-merchant-api`: host `localhost:7000`, path `/api/v1/tenants/{**catch-all}` (it also matches `/api/v1/tenants`).
  - `tenant-merchant-invitations`: host `localhost:7000`, path `/api/v1/merchant-invitations/{**catch-all}`.
  - Both forward to `https://localhost:7102/`. The merchant BFF's `Bff:DownstreamApiAddress` is then `https://localhost:7000/`.
- **`/internal/**` answers `404` on every host.**
- **Deployed routes** are not defined yet.
- **The Gateway does not authorize.** This service authorizes every request.

## 8. Health

`/health` and `/health/live` report liveness. `/health/ready` checks the database with a 5-second timeout and answers `503` (`Unhealthy`) without it. The body is the status word only.

## 9. Errors

Problem details (`application/problem+json`) carry `type`, `title`, `status`, `instance` (`"{METHOD} {path}"`), `traceId` and `correlationId`. `type` URIs use the placeholder scheme `https://errors.enterprisecommerce.example/tenant-merchant/{code}` (the final scheme is open, IDENTITY_API_CONTRACT.md section 4):

| Code | Status | Since |
|---|---|---|
| `validation-failed` | 400 | S1 |
| `tenant-not-found` | 404 | S1 |
| `permission-denied` | 403 | S1 |
| `tenant-slug-taken` | 409 | S1 |
| `idempotency-key-reused` | 422 | S1 |
| `membership-not-found` | 404 | S2 |
| `self-modification-forbidden` | 403 | S2 |
| `invalid-transition` | 409 | S2 |
| `last-active-owner` | 409 | S2 |
| `owner-role-reserved` | 422 | S2 |
| `concurrency-conflict` | 409 | S2 |
| `invitation-not-found` | 404 | S2 |
| `invitation-already-pending` | 409 | S2 |
| `invitation-limit-reached` | 409 | S2 |
| `invitation-not-pending` | 409 | S2 |
| `invitation-expired` | 410 | S2 |
| `invitation-email-mismatch` | 403 | S2 |
| `invitation-email-unverified` | 403 | S2 |
| `invitation-account-ineligible` | 403 | S2 |
| `already-a-member` | 409 | S2 |
| `identity-unavailable` | 503 | S2 |

Exception details are never returned.
