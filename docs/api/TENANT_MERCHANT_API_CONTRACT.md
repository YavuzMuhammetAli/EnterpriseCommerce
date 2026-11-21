# Tenant & Merchant API Contract

- **Status:** Accepted for Phase 3 slice S1, and implemented in S1. Later slices extend it. An endpoint exists only once its slice is merged.
- **Design:** [PHASE_3_TENANT_MERCHANT.md](../architecture/PHASE_3_TENANT_MERCHANT.md). Naming and the tenant-merchant model: [ADR-020](../decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md).
- **Conventions:** the cross-service conventions of [IDENTITY_API_CONTRACT.md section 1](IDENTITY_API_CONTRACT.md#1-conventions-baseline) apply: `/api/v1`, lowercase kebab-case paths, camelCase JSON, `X-Correlation-ID`, `Idempotency-Key`, and RFC 9457 problem details. Pagination follows [README.md](README.md#conventions).

## 1. Summary

| Endpoint | Caller | Section |
|---|---|---|
| `POST /internal/v1/tenants` | Allow-listed service client (scope `svc.tenant-merchant.provisioning`) | [3.1](#31-provision-a-tenant) |
| `GET /internal/v1/tenants/{tenantId}` | Allow-listed service client (same scope) | [3.2](#32-read-a-tenant-internal) |
| `GET /api/v1/tenants/{tenantId}` | Merchant-portal user who is an active member | [4.1](#41-read-a-tenant-member) |
| `GET /api/v1/tenants/{tenantId}/memberships` | Merchant-portal user with `MerchantOwner` or `MerchantAdmin` | [4.2](#42-list-memberships) |
| `GET /health`, `/health/live`, `/health/ready` | Anonymous | [6](#6-health) |

There is no public endpoint that creates a tenant or assigns a role. `MerchantOwner` is granted only by provisioning.

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

Creates, in **one** transaction, the tenant, its merchant profile and the initial `MerchantOwner` membership of an existing Identity subject.

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
    "createdAt": "2026-11-21T11:41:37.0853974+00:00"
  }
}
```

`ownerMembership` is the tenant's initial owner membership in its current state.

**Not verified:** this service cannot check that `ownerSubjectId` exists in Identity, which has no lookup API for it. The allow-listed caller is responsible for passing a real subject.

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

A non-GUID `tenantId` answers `404`.

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
  "membership": { "id": "…", "subjectId": "…", "role": "MerchantStaff", "status": "Active", "createdAt": "…" }
}
```

`membership` is the caller's own membership.

### 4.2 List memberships

`GET /api/v1/tenants/{tenantId}/memberships?page=1&pageSize=20`: `MerchantOwner` and `MerchantAdmin` only. `MerchantStaff` gets `403`.

- Current memberships only (`Active` and `Suspended`), oldest first.
- `page` is 1-based (default 1). `pageSize` defaults to 20 and is at most 100. Out-of-range or non-integer values get `400`.
- `200`: `{ "items": [membership, …], "page": 1, "pageSize": 20, "totalCount": 24 }`.

## 5. Gateway

- Development route `tenant-merchant-api`: host `localhost:7000`, path `/api/v1/tenants/{**catch-all}`, forwarded to `https://localhost:7102/`. The merchant BFF's `Bff:DownstreamApiAddress` is then `https://localhost:7000/`.
- `/internal/**` answers `404` on every host.
- Deployed routes are not defined yet.
- The Gateway does not authorize. This service authorizes every request.

## 6. Health

`/health` and `/health/live` report liveness. `/health/ready` checks the database with a 5-second timeout and answers `503` (`Unhealthy`) without it. The body is the status word only.

## 7. Errors

Problem details (`application/problem+json`) carry `type`, `title`, `status`, `instance` (`"{METHOD} {path}"`), `traceId` and `correlationId`. `type` URIs use the placeholder scheme `https://errors.enterprisecommerce.example/tenant-merchant/{code}` (the final scheme is open, IDENTITY_API_CONTRACT.md section 4):

- `validation-failed`
- `tenant-not-found`
- `permission-denied`
- `tenant-slug-taken`
- `idempotency-key-reused`

Exception details are never returned.
