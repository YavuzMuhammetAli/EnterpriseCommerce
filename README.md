# EnterpriseCommerce

A multi-tenant, multi-vendor e-commerce marketplace built with .NET 10 microservices. It is a long-term, portfolio-grade project, designed with production-inspired practices: clear service boundaries, data ownership, asynchronous integration, automated architecture rules and reproducible local infrastructure.

> **Status:**
>
> - **Phase 1** (repository and solution foundation) is complete and audited.
> - **Phase 2** (Identity) is implemented and tested locally:
>   - Phase 2.1 selected ASP.NET Core Identity with OpenIddict ([ADR-015](docs/decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)).
>   - Phase 2.2 accepted the authentication, token, session and browser client design ([ADR-016](docs/decisions/ADR-016-browser-client-architecture-backend-for-frontend.md) to [ADR-018](docs/decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)).
>   - Phase 2.3 implemented it in slices S1 to S7.
> - **Phase 3** (Tenant & Merchant) is in progress: slice S1, the service foundation and tenant provisioning, is implemented ([PHASE_3_TENANT_MERCHANT.md](docs/architecture/PHASE_3_TENANT_MERCHANT.md)).
>
> See [Current status](#current-status).

## Architecture summary

- **Ten business microservices:** Identity, Tenant & Merchant, Catalog, Inventory, Cart, Order, Payment, Fulfillment, Notification and Admin. See the [Service Catalog](docs/architecture/SERVICE_CATALOG.md).
- **YARP API Gateway** as the single entry point. It is infrastructure only, with no business logic.
- **Database per service.** No service accesses another service's data store. Catalog uses MongoDB, and all other services use SQL Server. Cart additionally uses Redis, for acceleration only.
- **Selective Clean Architecture** in every service: Domain → Application → Infrastructure → Api, enforced by architecture tests.
- **REST** for synchronous calls. **Kafka (KRaft)** carries asynchronous integration events and commands. The **transactional outbox** and **sagas** are the accepted directions, and their implementation is deferred.
- **Logical tenant isolation.** A tenant claim is never authorization proof on its own.
- **MVP scope:** single-merchant checkout. Payments are **simulated**, and card data is never collected or stored.

## Key documents

| Document | Purpose |
|---|---|
| [PROJECT_DECISIONS.md](docs/architecture/PROJECT_DECISIONS.md) | **Canonical** consolidated architecture and technology decisions |
| [CLAUDE.md](CLAUDE.md) | Operational rules for AI-assisted development sessions (reading order, rules, verification) |
| [SERVICE_CATALOG.md](docs/architecture/SERVICE_CATALOG.md) | The ten business services and their responsibilities |
| [SOLUTION_STRUCTURE.md](docs/architecture/SOLUTION_STRUCTURE.md) | Repository layout, layer rules and naming |
| [Architecture Decision Records](docs/decisions/README.md) | Historical record of each decision (ADR-001 to ADR-020) |
| [PHASE_1_FINAL_AUDIT.md](docs/architecture/PHASE_1_FINAL_AUDIT.md) | Phase 1 audit results and findings |
| [PHASE_2_IDENTITY_DESIGN_BRIEF.md](docs/architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md) | Identity design scope and Phase 2.3 implementation progress |
| [PHASE_3_TENANT_MERCHANT.md](docs/architecture/PHASE_3_TENANT_MERCHANT.md) | Tenant & Merchant design and implementation progress |
| [IDENTITY_API_CONTRACT.md](docs/api/IDENTITY_API_CONTRACT.md) | Identity API contract (accepted Phase 2.2 revision) and API conventions |
| [TENANT_MERCHANT_API_CONTRACT.md](docs/api/TENANT_MERCHANT_API_CONTRACT.md) | Tenant & Merchant API contract (Phase 3 S1) |

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
│   ├── Services/TenantMerchant/                       Tenant & Merchant, same four layers
│   ├── Bff/EnterpriseCommerce.Bff/                    backend-for-frontend (one per front end)
│   └── BuildingBlocks/                                shared technical libraries (none yet)
├── tests/
│   ├── Identity/EnterpriseCommerce.Identity.UnitTests/
│   ├── Identity/EnterpriseCommerce.Identity.IntegrationTests/
│   ├── TenantMerchant/EnterpriseCommerce.TenantMerchant.UnitTests/
│   ├── TenantMerchant/EnterpriseCommerce.TenantMerchant.IntegrationTests/
│   ├── Bff/ and Gateway/                              BFF and Gateway tests
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
| `http://localhost:5101/health/ready` | Readiness: database, keys, email adapter, background workers (status word only) |
| `http://localhost:5101/openapi/v1.json` | OpenAPI document (Development only) |

By default, Development uses ephemeral signing keys, so tokens end when Identity restarts. To keep them, and to rehearse the key rollover ([docs/security/IDENTITY_OPERATIONS.md](docs/security/IDENTITY_OPERATIONS.md)), generate a persisted development key ring outside the repository:

```bash
dotnet run deploy/dev/identity-dev-keys.cs -- init
. $env:USERPROFILE/.enterprisecommerce/identity-dev/identity-keys.ps1     # or: source ~/.enterprisecommerce/identity-dev/identity-keys.sh
```

## Run the Gateway

```bash
dotnet run --project src/Gateway/EnterpriseCommerce.Gateway --launch-profile http
```

| Endpoint | Description |
|---|---|
| `http://localhost:5000/health` | Gateway's own health check |
| `http://localhost:5000/identity/health` | Proxied to the Identity API (Development route, requires the Identity API to be running) |

The Development configuration also routes `storefront.localhost:7000`, `merchant.localhost:7000` and `admin.localhost:7000` to the three BFF deployments.

Routes are configured in the `ReverseProxy` section of `src/Gateway/EnterpriseCommerce.Gateway/appsettings*.json`. To point the Identity route elsewhere, override `ReverseProxy__Clusters__identity__Destinations__primary__Address`.

## Run the backend-for-frontend (BFF)

One codebase, three deployments (launch profiles `storefront`, `merchant` and `admin` on ports 7201 to 7203). Contract: [docs/api/BFF_API_CONTRACT.md](docs/api/BFF_API_CONTRACT.md).

```bash
dotnet run deploy/dev/bff-dev-keys.cs           # once: development client keys outside the repository
cd deploy/docker && docker compose --profile sqlserver --profile bff up -d --wait && cd ../..
dotnet user-secrets set ConnectionStrings:BffSessions "localhost:6380,password=<REDIS_SESSIONS_PASSWORD>" --project src/Bff/EnterpriseCommerce.Bff

# Identity, with the three BFF client registrations generated above:
. $env:USERPROFILE/.enterprisecommerce/bff-dev/identity-clients.ps1      # or: source ~/.enterprisecommerce/bff-dev/identity-clients.sh
dotnet run --project src/Services/Identity/EnterpriseCommerce.Identity.Api --launch-profile https
dotnet run --project src/Bff/EnterpriseCommerce.Bff --launch-profile storefront
dotnet run --project src/Gateway/EnterpriseCommerce.Gateway --launch-profile https
# Browse https://storefront.localhost:7000/bff/login
```

The BFF refuses to start unless the session Redis is reachable and uses `noeviction`. The browser flow through the Gateway needs a trusted local HTTPS certificate covering the `*.localhost` names: the default development certificate covers `localhost` only (finding I29 in [PHASE_2_2_AUTHENTICATION_FLOWS.md](docs/architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#117-implementation-findings-phase-23)).

## Run the Tenant & Merchant API

Ports: https 7102 and http 5102. Contract: [docs/api/TENANT_MERCHANT_API_CONTRACT.md](docs/api/TENANT_MERCHANT_API_CONTRACT.md).

```bash
cd deploy/docker && docker compose --profile sqlserver up -d --wait && cd ../..
dotnet user-secrets set ConnectionStrings:TenantMerchantDatabase "Server=localhost,1433;Database=EnterpriseCommerce_TenantMerchant;User Id=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True" --project src/Services/TenantMerchant/EnterpriseCommerce.TenantMerchant.Api
TenantMerchant__Database__ApplyMigrationsAtStartup=true dotnet run --project src/Services/TenantMerchant/EnterpriseCommerce.TenantMerchant.Api --launch-profile https
```

- Tokens are validated against Identity at `https://localhost:7101/` (`TenantMerchant:Authentication:Issuer`), so Identity must run to call the API.
- No client may provision tenants until one is allow-listed: `TenantMerchant__Provisioning__AllowedClientIds__0=svc-<caller>`, plus a matching service client in Identity ([contract section 3](docs/api/TENANT_MERCHANT_API_CONTRACT.md#3-internal-api-service-callers)).
- The Gateway's Development route forwards `https://localhost:7000/api/v1/tenants/**` to it.

## Run local infrastructure (Docker Compose profiles)

```bash
cd deploy/docker
docker compose --profile sqlserver up -d --wait      # SQL Server only
docker compose --profile mongodb up -d --wait        # MongoDB only
docker compose --profile redis up -d --wait          # Redis only (cache, allkeys-lru)
docker compose --profile bff up -d --wait            # BFF session Redis only (noeviction)
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
- Architecture documentation and ADR-001 to ADR-014, with the consolidated decisions in [PROJECT_DECISIONS.md](docs/architecture/PROJECT_DECISIONS.md).
- Phase 1 final audit ([PHASE_1_FINAL_AUDIT.md](docs/architecture/PHASE_1_FINAL_AUDIT.md)).

**Decided (Phase 2.1 and 2.2, documentation):** the Identity provider is ASP.NET Core Identity with OpenIddict, using OAuth 2.0 and OpenID Connect ([ADR-015](docs/decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)). Every front end uses a backend-for-frontend ([ADR-016](docs/decisions/ADR-016-browser-client-architecture-backend-for-frontend.md)). The token, session and protocol strategy is in [ADR-017](docs/decisions/ADR-017-token-session-and-protocol-strategy.md), and account security tokens and security email delivery are in [ADR-018](docs/decisions/ADR-018-account-security-tokens-and-security-email-delivery.md).

**Implemented (Phase 2.3, Identity):** slices S1 to S7: persistence, authentication and tokens, registration and email confirmation, session revocation and password management, routing, account lifecycle, key rollover, operations, and the BFF. They are tested locally and not production-verified. Progress: [PHASE_2_IDENTITY_DESIGN_BRIEF.md](docs/architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md#phase-23-implementation-progress).

**Implemented (Phase 3 S1, Tenant & Merchant):** the service foundation with its own SQL Server database, and tenant provisioning.

- **Provisioning** creates the tenant, its merchant profile and the initial `MerchantOwner` membership in one transaction. It is idempotent, and only allow-listed service clients may call it.
- **The member API** is authorized from the service's own membership data.

Naming and model: [ADR-020](docs/decisions/ADR-020-tenant-merchant-naming-and-tenant-merchant-cardinality.md). No integration event yet.

**Not implemented yet:**

- Invitations, membership management, merchant applications, subscriptions and usage (later Tenant & Merchant slices)
- Product catalog, inventory operations, cart, checkout, orders, payments, fulfillment, notifications and admin features
- MongoDB persistence
- Kafka topics and event flows, the outbox pattern and sagas
- Redis usage in any business service (the BFF session store is implemented in Phase 2.3 S7)
- Application containers and Dockerfiles
- OpenTelemetry, Prometheus, Grafana and Loki (planned as the observability phase)
- The other eight business services (only documented so far)

## Roadmap (indicative)

1. **Phase 1:** repository and solution foundation *(complete, audited)*
2. **Phase 2:** Identity design and implementation *(implemented and tested locally; production gates open, see [brief](docs/architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md))*
3. Observability foundation: OpenTelemetry, Collector, Prometheus, Grafana, Loki *(not started; Tenant & Merchant was authorized first)*
4. Tenant & Merchant *(Phase 3, in progress: S1 done)*, Catalog and Inventory
5. Cart, Order and simulated Payment, for single-merchant checkout
6. Fulfillment, Notification and Admin
7. Multi-merchant checkout

The order of later phases is subject to review.
