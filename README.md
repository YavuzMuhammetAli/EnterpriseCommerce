# EnterpriseCommerce

A multi-tenant, multi-vendor e-commerce marketplace built with .NET 10 microservices. It is a long-term, portfolio-grade project, designed with production-inspired practices: clear service boundaries, data ownership, asynchronous integration, automated architecture rules and reproducible local infrastructure.

> **Status: Phase 1 (repository and solution foundation).** No business functionality is implemented yet. See [Current status](#current-status).

## Architecture summary

- **Ten business microservices:** Identity, Tenant & Merchant, Catalog, Inventory, Cart, Order, Payment, Fulfillment, Notification and Admin. See the [Service Catalog](docs/architecture/SERVICE_CATALOG.md).
- **YARP API Gateway** as the single entry point. It is infrastructure only, with no business logic.
- **Database per service.** No service accesses another service's data store.
- **Selective Clean Architecture** in every service: Domain → Application → Infrastructure → Api, enforced by architecture tests.
- **Kafka (KRaft)** for asynchronous integration events. **Redis** for ephemeral and cached data only.
- **MVP scope:** single-merchant checkout. Payments are **simulated**, and card data is never collected or stored.

Details: [Solution Structure](docs/architecture/SOLUTION_STRUCTURE.md) · [Architecture Decision Records](docs/decisions/README.md)

## Technology stack

| Area | Technology |
|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core Web API |
| Data | Entity Framework Core, SQL Server, MongoDB |
| Caching | Redis |
| Messaging | Apache Kafka (KRaft mode) |
| Gateway | YARP |
| Logging and observability | Serilog *(in use)*; OpenTelemetry, Prometheus, Grafana, Loki *(planned)* |
| Testing | xUnit v3 |
| Local environment | Docker, Docker Compose |
| CI | GitHub Actions |

## Repository structure

```text
EnterpriseCommerce/
├── src/
│   ├── Gateway/EnterpriseCommerce.Gateway/            YARP gateway
│   ├── Services/Identity/
│   │   ├── EnterpriseCommerce.Identity.Api/           composition root, HTTP
│   │   ├── EnterpriseCommerce.Identity.Application/   use cases
│   │   ├── EnterpriseCommerce.Identity.Domain/        business rules (pure C#)
│   │   └── EnterpriseCommerce.Identity.Infrastructure/
│   └── BuildingBlocks/                                shared technical libraries (none yet)
├── tests/
│   ├── Identity/EnterpriseCommerce.Identity.UnitTests/
│   ├── Identity/EnterpriseCommerce.Identity.IntegrationTests/
│   └── ArchitectureTests/EnterpriseCommerce.ArchitectureTests/
├── deploy/docker/                                     Compose file for local infrastructure
├── docs/                                              architecture, decisions, api, events, security, testing
├── .github/workflows/ci.yml
├── Directory.Build.props / Directory.Packages.props   shared build settings, central package versions
├── global.json                                        .NET SDK pin and test runner
└── EnterpriseCommerce.sln
```

## Prerequisites

- **.NET SDK 10.0.301** or a later 10.0 feature band (pinned in `global.json` with `rollForward: latestFeature`)
- **Docker Desktop** (or another Docker Engine) with **Docker Compose v2+**, running, for the infrastructure profiles
- **Git**
- About 5 GB of free RAM if you start all infrastructure profiles at once. Single profiles need much less.

## Initial setup

```bash
git clone <your-repository-url> EnterpriseCommerce
cd EnterpriseCommerce
dotnet restore EnterpriseCommerce.sln

# Local infrastructure credentials (placeholders; .env is gitignored)
cp deploy/docker/.env.example deploy/docker/.env
```

## Build

```bash
dotnet build EnterpriseCommerce.sln -c Release
```

Warnings are treated as errors. Code style (`.editorconfig`), analyzers and NuGet vulnerability auditing run on every build.

## Run the Identity API

```bash
dotnet run --project src/Services/Identity/EnterpriseCommerce.Identity.Api --launch-profile http
```

| Endpoint | Description |
|---|---|
| `http://localhost:5101/health` | Health check, returns `Healthy` |
| `http://localhost:5101/openapi/v1.json` | OpenAPI document (Development only) |

## Run the Gateway

```bash
dotnet run --project src/Gateway/EnterpriseCommerce.Gateway --launch-profile http
```

| Endpoint | Description |
|---|---|
| `http://localhost:5000/health` | Gateway's own health check |
| `http://localhost:5000/identity/health` | Proxied to the Identity API (Development route, requires the Identity API to be running) |

Routes are configured in the `ReverseProxy` section of `src/Gateway/EnterpriseCommerce.Gateway/appsettings*.json`. To point the Identity route elsewhere, override `ReverseProxy__Clusters__identity__Destinations__primary__Address`.

## Run local infrastructure (Docker Compose profiles)

```bash
cd deploy/docker
docker compose --profile sqlserver up -d --wait      # SQL Server only
docker compose --profile mongodb up -d --wait        # MongoDB only
docker compose --profile redis up -d --wait          # Redis only
docker compose --profile kafka up -d --wait          # Kafka (KRaft) only
docker compose --profile all up -d --wait            # everything
docker compose --profile all ps
docker compose --profile all down                    # stop (add -v to delete data volumes)
```

No service starts without a profile. Images, ports, memory limits and connection details are in [deploy/docker/README.md](deploy/docker/README.md). The services don't use this infrastructure yet.

## Run tests

```bash
dotnet test --solution EnterpriseCommerce.sln -c Release
```

The suite contains unit tests (exception handling), integration tests (the real API pipeline in memory: health, OpenAPI, problem details) and architecture tests (layer dependency rules, forbidden technologies in inner layers, no cross-service references, naming). See [docs/testing](docs/testing/README.md).

## Current status

**Implemented (Phase 1):**

- Solution and repository standards: `.sln`, Central Package Management, nullable reference types, implicit usings, analyzers, warnings as errors, `.editorconfig`, LF line endings, and NuGet auditing.
- Identity service skeleton (four layers) with a health endpoint, OpenAPI, global exception handling (problem details), DI composition and structured logging (Serilog, compact JSON).
- YARP Gateway skeleton with a health endpoint and an example Identity route from configuration.
- Docker Compose profiles for SQL Server, MongoDB, Redis and Kafka (KRaft): pinned images, named volumes, health checks and memory limits.
- Unit, integration and architecture tests, and a GitHub Actions CI workflow.
- Architecture documentation and ADR-001 to ADR-010.

**Not implemented yet (explicitly out of scope for Phase 1):**

- User registration, login, password management, JWT/token issuance and refresh token rotation (the identity protocol and provider are not yet chosen)
- Authentication and authorization configuration
- Tenant and merchant management
- Product catalog, inventory operations, cart, checkout, orders, payments, fulfillment, notifications and admin features
- Any persistence (EF Core / MongoDB) and any database schema
- Kafka topics and event flows, the outbox pattern and sagas
- Redis usage in any service
- Application containers and Dockerfiles
- OpenTelemetry, Prometheus, Grafana and Loki (planned as the observability phase)
- The other nine business services (only documented so far)

## Roadmap (indicative)

1. **Phase 1:** repository and solution foundation *(this phase)*
2. Identity design: protocol and provider, token strategy, data model
3. Observability foundation: OpenTelemetry, Collector, Prometheus, Grafana, Loki
4. Tenant & Merchant, Catalog and Inventory
5. Cart, Order and simulated Payment, for single-merchant checkout
6. Fulfillment, Notification and Admin
7. Multi-merchant checkout

The order of later phases is subject to review.
