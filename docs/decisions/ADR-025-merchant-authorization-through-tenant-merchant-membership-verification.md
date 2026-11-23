# ADR-025: Merchant Authorization through Tenant & Merchant Membership Verification

- **Status:** Accepted
- **Date:** 2026-11-23
- **Phase:** 4 (Catalog)
- **Related:**
  - [ADR-014](ADR-014-logical-tenant-isolation.md) (rules 3 and 4)
  - [ADR-017](ADR-017-token-session-and-protocol-strategy.md) (sections 2 to 4: claims, audiences, resource-server validation)
  - [ADR-021](ADR-021-merchant-invitations-and-membership-administration.md) (the Identity verdict pattern)
  - [ADR-024](ADR-024-catalog-service-design.md) (Catalog)
- **Resolves, for Catalog:** the design item "the membership data shared with other services" of PROJECT_DECISIONS section 7 (synchronous verification, no shared data). It also resolves Phase 3 finding P3-3 for token validation.
- **Design and evidence:** [PHASE_4_CATALOG.md](../architecture/PHASE_4_CATALOG.md) section 6.

## Context

Catalog must let only active merchant members manage their tenant's products and brands, according to their merchant role.

The relevant facts:

- **Access tokens carry no tenant and no role** (ADR-017 section 2). A merchant user's token carries the merchant front end's audience, `ec-merchant`.
- **A tenant ID or claim is never proof** (ADR-014 rule 4).
- **Tenant & Merchant is the authority** for memberships and roles.
- **ADR-014 rule 3 allows a local read model** built from integration events, but there are no membership events. Tenant & Merchant deferred them to the slice of their first consumer, and TENANT_MERCHANT_EVENTS.md explains that `TenantCreated` alone would authorize wrongly. Phase 4 also excludes consumers and inboxes.
- **No existing endpoint answers the question.** Tenant & Merchant's member API refuses service tokens. Its only internal endpoints serve provisioning.

ADR-017 section 4 says that a BuildingBlock for the shared JWT bearer configuration is introduced "once a second service needs it". Catalog is the second resource server with that configuration.

## Decision

### 1. A narrow internal verification endpoint in Tenant & Merchant

`POST /internal/v1/tenants/{tenantId}/membership-verifications` answers whether a subject is an active member of a tenant, and with which role. The request body is:

```json
{ "subjectId": "<Identity sub>" }
```

- **The subject goes in the body**, never the URL, so it does not appear in request logs. Unknown fields answer `400`.
- **The answer** is `200` with `{ "active": true, "role": "MerchantOwner" | "MerchantAdmin" | "MerchantStaff" }` or `{ "active": false, "role": null }`.
  - `active` means an `Active` membership in an `Active` tenant.
  - A missing tenant, a missing membership and a suspended or removed membership all give the same negative verdict.
  - The answer contains nothing else: no membership ID, no audit data.
- **Caller requirements:**
  - a service token (`ec_ptype=service`, `sub == client_id`, `svc-` prefix)
  - the audience `tenant-merchant-api`
  - the scope `svc.tenant-merchant.membership-verification`
  - a client listed in `TenantMerchant:MembershipVerification:AllowedClientIds`. The list is empty by default, and startup refuses an entry without the `svc-` prefix.

  The policy is separate from provisioning. A client allowed for one is not allowed for the other.
- **Routing.** Like every internal path, the endpoint is never routed by the Gateway.

### 2. Catalog verifies on every merchant request, without caching the verdict

- **The flow.** Every merchant request:
  1. derives the subject from the validated `ec-merchant` user token
  2. takes the tenant from the route
  3. asks Tenant & Merchant once, as `svc-catalog`
- **The service token.** Catalog uses the client credentials grant with `private_key_jwt`, as Tenant & Merchant does towards Identity.
  - The *service token* is cached until 60 seconds before it expires, and invalidated after a `401`, with one retry.
  - **The verdict is never cached.** A suspension, removal or role change applies to the next request.
- **Correlation.** The `X-Correlation-ID` is propagated to Tenant & Merchant.
- **Fail closed.** Any of the following answers `503 membership-unavailable` with `Retry-After`:
  - no verification client is configured, which is allowed only in Development
  - a timeout, the default being 5 seconds
  - a transport error
  - a non-`200` status
  - an unknown JSON shape
  - a token failure

  Nothing is read or written for the request.
- **The answer to the caller:**
  - No active membership answers `404 tenant-not-found`, indistinguishable from an unknown tenant.
  - An insufficient role answers `403 permission-denied`.
  - Every query and write is also scoped by the route's tenant, so another tenant's resource answers `404`.
- **Permissions are Catalog's own**, mapped from the authoritative role:

  | Permission | `MerchantOwner` | `MerchantAdmin` | `MerchantStaff` |
  |---|---|---|---|
  | `ViewCatalog` (read products and brands) | yes | yes | yes |
  | `EditProducts` (create and change product content, variants, attributes, media) | yes | yes | yes |
  | `PublishProducts` (publish, unpublish, archive, restore) | yes | yes | no |
  | `ManageBrands` | yes | yes | no |

  An unknown role value grants nothing and is logged.

### 3. Shared token validation: `EnterpriseCommerce.BuildingBlocks.Authentication`

**Contents.** This is technical code only, with no policies and no business rules:

- the ADR-017 section 4 JWT bearer configuration:
  - exact issuer and audiences
  - RS256
  - `typ` `at+jwt`
  - 30 seconds of skew
  - no inbound claim mapping
  - a role claim type that no token carries
- the caller claim helpers:
  - service client
  - user subject
  - audience
  - scope

**Usage rules:**

- **Configuration stays with the service.** Each service binds and validates its own configuration section and passes the values in.
- **Only `*.Api` projects** may reference this block.
- **Tenant & Merchant moves to it** with unchanged behaviour. Catalog uses it from the start.

## Consequences

- **Positive:** membership changes take effect immediately in Catalog. Catalog stores no membership data and no personal data beyond the subject reference in audit fields. The verification contract is minimal and explicit.
- **Positive:** one reviewed implementation of resource-server token validation instead of copies.
- **Negative: an availability coupling.** Merchant catalog management is unavailable while Tenant & Merchant is. Public discovery is not affected.
- **Negative: latency.** Every merchant request adds one internal HTTP call.
- **Future work.** When membership events exist, a local projection (ADR-014 rule 3) can replace the call. That needs its own decision on staleness and revocation.
- **Negative: tenant status is invisible to Catalog.** A suspended tenant's published products stay publicly visible until tenant status reaches Catalog, through an event or a call. Merchant writes are blocked, because the verdict requires an active tenant. Tenant suspension itself does not exist yet.

## Alternatives considered

- **A local membership projection from events:** this needs membership events, a consumer and an inbox, all deferred or excluded from Phase 4. Rejected for now.
- **Calling the member API with the user's token:** that API answers Tenant & Merchant's own questions (tenant details), not a role verdict for another service. Acting for the user is out of MVP scope (token exchange, PHASE_2_2 section 5.7). Rejected.
- **Caching verdicts for a short time:** revocation would be delayed by up to the cache period without a designed invalidation. Rejected (no cache without designed revocation).
- **`GET` with the subject in the path:** the subject ID would appear in request logs. Rejected.
- **Returning `404` for a negative verdict:** it would be indistinguishable from a routing or deployment error, which must fail closed as `503`. Rejected.
- **Copying the JWT bearer configuration into Catalog:** this contradicts ADR-017 section 4. Rejected.
