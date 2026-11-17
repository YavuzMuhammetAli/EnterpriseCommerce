# API Documentation

API conventions and per-service API documentation go here once the first business endpoints exist.

## Contracts

| Contract | Status |
|---|---|
| [IDENTITY_API_CONTRACT.md](IDENTITY_API_CONTRACT.md) | Phase 0.3 baseline, preserved. Not a final specification; reviewed in Phase 2. It also defines the cross-service API conventions (`/api/v1`, headers, `X-Correlation-ID`, `Idempotency-Key`, RFC 9457 problem details). |

Current state:

- Each service API generates an OpenAPI document with the built-in ASP.NET Core OpenAPI support, served at `/openapi/v1.json` in the Development environment only.
- Errors use RFC 9457 problem details (`application/problem+json`), including a `traceId` for correlation.
- External clients reach services through the Gateway (for example `http://localhost:5000/identity/...`).

Planned topics: versioning strategy, resource naming, pagination, idempotency keys, and error catalog.
