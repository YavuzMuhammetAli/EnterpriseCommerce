# API Documentation

API conventions and per-service API documentation.

## Contracts

| Contract | Status |
|---|---|
| [IDENTITY_API_CONTRACT.md](IDENTITY_API_CONTRACT.md) | Accepted (Phase 2.2 revision, [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) and [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)). Implemented incrementally in Phase 2.3. It also defines the cross-service API conventions (`/api/v1`, headers, `X-Correlation-ID`, `Idempotency-Key`, RFC 9457 problem details). |
| [BFF_API_CONTRACT.md](BFF_API_CONTRACT.md) | Implemented in Phase 2.3 S7 ([ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md)). |
| [TENANT_MERCHANT_API_CONTRACT.md](TENANT_MERCHANT_API_CONTRACT.md) | Accepted and implemented for Phase 3 S1 (internal tenant provisioning, member reads) S2 (membership administration, invitations, [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md)) and S3 (self-service onboarding, [ADR-022](../decisions/ADR-022-self-service-tenant-onboarding.md)), and for Phase 4 the internal membership verification ([ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md)). |
| [CATALOG_API_CONTRACT.md](CATALOG_API_CONTRACT.md) | Accepted and implemented in Phase 4 ([ADR-024](../decisions/ADR-024-catalog-service-design.md), [ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md)): public discovery, merchant products and brands, the internal category taxonomy. Phase 5 adds the internal variant verification ([ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md)). |
| [INVENTORY_API_CONTRACT.md](INVENTORY_API_CONTRACT.md) | Accepted and implemented in Phase 5 ([ADR-026](../decisions/ADR-026-inventory-service-design.md)): merchant stock management and the internal reservation API. |

## Conventions

The cross-service conventions are in [IDENTITY_API_CONTRACT.md section 1](IDENTITY_API_CONTRACT.md#1-conventions-baseline). Added since:

- **Pagination** (first used by Tenant & Merchant, Phase 3 S1):
  - Query parameters `page` (1-based, default 1) and `pageSize` (default 20, at most 100).
  - Out-of-range or non-integer values answer `400` with problem details.
  - Responses are `{ "items": [...], "page": 1, "pageSize": 20, "totalCount": 42 }`.
- **`Idempotency-Key` retention:** decided per endpoint. Tenant provisioning keeps the key on the created tenant for the tenant's lifetime ([TENANT_MERCHANT_API_CONTRACT.md section 3.1](TENANT_MERCHANT_API_CONTRACT.md#31-provision-a-tenant)).
- **Internal endpoints** live under `/internal/v1/...`, accept only service tokens of allow-listed `svc-` clients, and are never routed by the Gateway.
- **Optimistic concurrency over HTTP** (first used by Catalog, Phase 4, [ADR-024](../decisions/ADR-024-catalog-service-design.md) section 14): reads return `ETag: "<version>"`; a full replacement requires `If-Match` (`428` without it, `412` when stale); a race lost on the server answers `409 concurrency-conflict`. State commands need no tag.
- **Request body limit** (Catalog): 256 KiB, `413` above it.
- **Single-use secrets in responses** (first used by invitation creation, Phase 3 S2): returned once, with `Cache-Control: no-store`, never by a read, and sent back only in a request body, never in a URL.

## Current state

- Each service API generates an OpenAPI document with the built-in ASP.NET Core OpenAPI support, served at `/openapi/v1.json` in the Development environment only.
- Errors use RFC 9457 problem details (`application/problem+json`), including a `traceId` for correlation. Tenant & Merchant and Catalog also add `correlationId`.
- External clients reach services through the Gateway. Identity is reached on its own host name (ADR-017). The Phase 1 `/identity` path-prefix route stays for development diagnostics only.

Planned topics: versioning strategy details, and an error catalog.
