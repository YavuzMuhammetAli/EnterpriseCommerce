# Phase 6: Cart

- **Status:** in progress (authorized 2026-11-24).
- **Decisions:**
  - [ADR-029](../decisions/ADR-029-cart-service-design.md) (Cart design)
  - [ADR-030](../decisions/ADR-030-cart-redis-cache-consistency.md) (the Redis cache and its consistency)
- **Contract:** [CART_API_CONTRACT.md](../api/CART_API_CONTRACT.md). It depends on Catalog's public product read, [CATALOG_API_CONTRACT.md section 2.2](../api/CATALOG_API_CONTRACT.md#22-read-a-published-product).
- **Operations:** [CART_OPERATIONS.md](../operations/CART_OPERATIONS.md) (written with the implementation).

## 1. Inputs and scope

**Inputs:**

- the owner's Phase 6 authorization of 2026-11-24
- SERVICE_CATALOG section 5
- PROJECT_DECISIONS sections 5, 6, 8 and 11
- ADR-006, ADR-011, ADR-014, ADR-016, ADR-017, ADR-024 and ADR-026
- the Identity, BFF, Catalog, Inventory and Gateway implementations, which were read rather than assumed

**In scope:**

- the Cart service: four layers under `src/Services/Cart`, with its own SQL Server database
- a Redis cache on the existing cache Redis
- the customer cart API
- the Catalog product snapshot through the public read
- cart expiry and its cleanup
- the Gateway route, CI support and local development settings

**Out of scope, by the authorization and the ADRs:**

- stock reservation, or any call to Inventory
- checkout, orders, payments and fulfillment
- saga code (ADR-013 rule 9)
- guest carts and multi-merchant carts
- integration events (Cart has no consumer, and no event is needed yet)
- application containers, as in every earlier phase

## 2. Plan

| # | Milestone |
|---|---|
| 1 | Decisions and contracts (ADR-029, ADR-030, the API contract) |
| 2 | Service foundation: projects, host, security, health, architecture tests |
| 3 | Domain and SQL Server persistence |
| 4 | Catalog product snapshots, and a consumer-contract test in Catalog |
| 5 | The customer endpoints, with security and concurrency tests |
| 6 | The Redis cache |
| 7 | The Gateway route, CI and local settings |
| 8 | Measurements and the full regression |
| 9 | Phase closure |
