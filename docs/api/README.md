# API Documentation

API conventions and per-service API documentation go here once the first business endpoints exist.

## Contracts

| Contract | Status |
|---|---|
| [IDENTITY_API_CONTRACT.md](IDENTITY_API_CONTRACT.md) | Accepted (Phase 2.2 revision, [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) and [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)). Implemented incrementally in Phase 2.3. It also defines the cross-service API conventions (`/api/v1`, headers, `X-Correlation-ID`, `Idempotency-Key`, RFC 9457 problem details). |

Current state:

- Each service API generates an OpenAPI document with the built-in ASP.NET Core OpenAPI support, served at `/openapi/v1.json` in the Development environment only.
- Errors use RFC 9457 problem details (`application/problem+json`), including a `traceId` for correlation.
- External clients reach services through the Gateway. Identity is reached on its own host name (ADR-017). The Phase 1 `/identity` path-prefix route stays for development diagnostics only.

Planned topics: versioning strategy, resource naming, pagination, idempotency keys, and error catalog.
