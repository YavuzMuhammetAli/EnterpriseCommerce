# Catalog service

`EnterpriseCommerce.Catalog` owns what can be sold and how it is described and found:
- products with their variants, SKUs, list prices, attributes and media references
- the platform category taxonomy
- tenant brands
- the publication lifecycle
- public discovery

It stores all of it in its own MongoDB database. It holds no stock data (Inventory) and no order data (Order).

| Project | Contents |
|---|---|
| `EnterpriseCommerce.Catalog.Domain` | Aggregates `Product`, `Category` and `Brand`, and value objects. No dependencies. |
| `EnterpriseCommerce.Catalog.Application` | Use cases, store ports, merchant authorization (`CatalogAccessAuthorizer`), meters |
| `EnterpriseCommerce.Catalog.Infrastructure` | MongoDB persistence (the only MongoDB driver code of the platform), index setup, readiness, and the Tenant & Merchant membership client |
| `EnterpriseCommerce.Catalog.Api` | Host, security policies, endpoints, problem details |

**Ports:** `https://localhost:7103` and `http://localhost:5103`. The Gateway route is `/api/v1/catalog/**` on `localhost:7000`.

**Documents:**

| Topic | Document |
|---|---|
| Design, decisions and evidence | [PHASE_4_CATALOG.md](../../../docs/architecture/PHASE_4_CATALOG.md), [ADR-024](../../../docs/decisions/ADR-024-catalog-service-design.md), [ADR-025](../../../docs/decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md) |
| API | [CATALOG_API_CONTRACT.md](../../../docs/api/CATALOG_API_CONTRACT.md) |
| Running, configuration and troubleshooting | [CATALOG_OPERATIONS.md](../../../docs/operations/CATALOG_OPERATIONS.md) |
| Tests | `tests/Catalog` (MongoDB tests need `EC_TEST_MONGODB`; see [docs/testing](../../../docs/testing/README.md)) |
