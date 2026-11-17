# Solution Structure

This document describes how the EnterpriseCommerce repository is organized, and the rules that keep services independent. These rules are enforced automatically by `tests/ArchitectureTests`.

Related documents: [PROJECT_DECISIONS.md](PROJECT_DECISIONS.md) (the canonical decisions), [SERVICE_CATALOG.md](SERVICE_CATALOG.md), [PHASE_1_FINAL_AUDIT.md](PHASE_1_FINAL_AUDIT.md), [PHASE_2_IDENTITY_DESIGN_BRIEF.md](PHASE_2_IDENTITY_DESIGN_BRIEF.md) and [CLAUDE.md](../../CLAUDE.md).

## Repository layout

```text
EnterpriseCommerce/
├── src/
│   ├── Gateway/EnterpriseCommerce.Gateway/          YARP API Gateway (infrastructure, not a business service)
│   ├── Services/{Service}/EnterpriseCommerce.{Service}.{Layer}/
│   └── BuildingBlocks/                              shared technical libraries (none yet; see policy below)
├── tests/
│   ├── {Service}/EnterpriseCommerce.{Service}.UnitTests/
│   ├── {Service}/EnterpriseCommerce.{Service}.IntegrationTests/
│   └── ArchitectureTests/EnterpriseCommerce.ArchitectureTests/
├── deploy/docker/                                   Docker Compose for local infrastructure
├── docs/                                            architecture, decisions (ADRs), api, events, security, testing
└── .github/workflows/                               CI
```

Repository-wide build settings live in `Directory.Build.props`, and every package version lives in `Directory.Packages.props` (Central Package Management). Individual project files declare only their references.

## Microservice boundaries

Each business service (see [SERVICE_CATALOG.md](SERVICE_CATALOG.md)) is an independently deployable unit with:

- its own folder under `src/Services/{Service}/`,
- its own four layer projects,
- its own data store, which no other service accesses directly ([ADR-003](../decisions/ADR-003-database-per-service.md)),
- its own test projects under `tests/{Service}/`.

Services communicate only through:

- **synchronous HTTP APIs**, usually reached through the Gateway, and
- **asynchronous integration events and commands** over Kafka ([ADR-005](../decisions/ADR-005-kafka-for-asynchronous-communication.md), [ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md)).

A service never references another service's projects. The same type name in two services (for example `Product` in Catalog and in Order) is intentional. Each one is that service's own model of the concept.

## Layers and responsibilities

The structure is a selective Clean Architecture per service ([ADR-002](../decisions/ADR-002-clean-architecture-per-service.md)).

| Layer | Project | Responsibilities |
|---|---|---|
| **Domain** | `EnterpriseCommerce.{Service}.Domain` | Entities, aggregates, value objects, domain events, business rules, domain exceptions. Pure C#, with no infrastructure. |
| **Application** | `EnterpriseCommerce.{Service}.Application` | Use cases, and commands and queries where they add value ([ADR-009](../decisions/ADR-009-selective-cqrs.md)). Also handlers, the interfaces that infrastructure implements, validation and orchestration. |
| **Infrastructure** | `EnterpriseCommerce.{Service}.Infrastructure` | EF Core / MongoDB persistence, entity configurations, repository implementations where justified, Kafka/Redis integrations, and external service adapters. |
| **Api** | `EnterpriseCommerce.{Service}.Api` | HTTP endpoints, request and response contracts, middleware, authentication and authorization configuration, OpenAPI, and DI composition (the composition root). |

Patterns are applied only where they earn their keep. There are no generic repositories over every table, and no mandatory mediator for trivial reads.

## Dependency direction

```text
            ┌──────────────┐
            │     Api      │  composition root
            └──┬────────┬──┘
               │        │
               ▼        ▼
   ┌──────────────┐  ┌────────────────┐
   │ Application  │◀─│ Infrastructure │  implements Application interfaces
   └──────┬───────┘  └───────┬────────┘
          │                  │
          ▼                  ▼
        ┌──────────────────────┐
        │        Domain        │  no dependencies
        └──────────────────────┘
```

| Project | May reference |
|---|---|
| Domain | nothing (no project references, no packages, no framework references) |
| Application | its own service's Domain, plus BuildingBlocks |
| Infrastructure | its own service's Application and Domain, plus BuildingBlocks |
| Api | its own service's Application and Infrastructure, plus BuildingBlocks |
| Gateway | no service project, only BuildingBlocks |
| BuildingBlocks | other BuildingBlocks only |

Domain and Application must not reference EF Core, SQL Server or MongoDB drivers, Kafka, Redis, ASP.NET Core, YARP or Serilog. The architecture tests check this in two ways:

1. **Declared dependencies.** Every `src/**/*.csproj` is parsed for `ProjectReference`, `PackageReference` and `FrameworkReference`. This catches a violation as soon as the reference is added, even before code uses it.
2. **Compiled dependencies.** The referenced assemblies of the built layer assemblies are inspected. This catches actual usage.

## Gateway responsibility

`EnterpriseCommerce.Gateway` is a YARP reverse proxy ([ADR-007](../decisions/ADR-007-yarp-api-gateway.md)). It provides:

- one external entry point and route mapping (`/identity/**` → Identity API, and so on),
- cross-cutting edge concerns as they are introduced: TLS termination, rate limiting, request size limits, correlation headers, token *validation* at the edge,
- its own health endpoint (`/health`), which is never proxied.

The Gateway must **not** contain business logic, aggregate data across services, issue tokens, or reference any service project. Routes and clusters are configuration (`ReverseProxy` section). The example Identity route lives in `appsettings.Development.json`.

## BuildingBlocks policy

`src/BuildingBlocks/` holds **shared technical** libraries only, and only when there is a concrete need, such as the same code needed by at least two services.

- Allowed, when needed: logging/telemetry setup helpers, health-check conventions, messaging plumbing, and common ASP.NET Core middleware.
- **Never allowed:** shared domain models, shared business entities, shared DTOs that couple service contracts, or anything that forces services to deploy together.
- A Domain project never references BuildingBlocks, because a shared domain kernel is explicitly rejected.
- Project naming is `EnterpriseCommerce.BuildingBlocks.{Concern}`.

Some duplication between services (for example, the small exception handler in each API) is accepted until a second real consumer justifies extraction.

## Naming conventions

| Item | Convention | Example |
|---|---|---|
| Solution | `EnterpriseCommerce.sln` | |
| Service projects | `EnterpriseCommerce.{Service}.{Layer}` | `EnterpriseCommerce.Identity.Application` |
| Gateway | `EnterpriseCommerce.Gateway` | |
| Building blocks | `EnterpriseCommerce.BuildingBlocks.{Concern}` | `EnterpriseCommerce.BuildingBlocks.Observability` (hypothetical) |
| Unit tests | `EnterpriseCommerce.{Service}.UnitTests` | |
| Integration tests | `EnterpriseCommerce.{Service}.IntegrationTests` | |
| Namespaces | Follow the project name and folder path | `EnterpriseCommerce.Identity.Api.ExceptionHandling` |
| Project folder | Same as the project name | `src/Services/Identity/EnterpriseCommerce.Identity.Api/` |

`{Service}` is PascalCase with no spaces or symbols. The planned names are listed in [SERVICE_CATALOG.md](SERVICE_CATALOG.md).

## Local ports (development)

| Host | URL |
|---|---|
| Gateway | `http://localhost:5000` |
| Identity API | `http://localhost:5101` |

Future services get the next free port in the `51xx` range, recorded in this table.
