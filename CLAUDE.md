# CLAUDE.md: EnterpriseCommerce

The operational instruction manual for Claude Code sessions in this repository. Read it at the start of every session. **Architectural intent lives in the documents listed below. This file tells you how to work, and does not restate them.**

## Project identity

- **Product:** a portfolio-grade, enterprise-oriented, multi-tenant, multi-vendor e-commerce marketplace.
- **Stack:**
  - .NET 10 LTS, ASP.NET Core Web API, EF Core
  - SQL Server, MongoDB, Redis, Apache Kafka (KRaft)
  - YARP
  - Docker and Docker Compose
  - Serilog, with OpenTelemetry, Prometheus, Grafana and Loki planned
  - xUnit v3, GitHub Actions
- **Style:**
  - ten business microservices plus a YARP Gateway (infrastructure)
  - selective Clean Architecture and DDD, selective CQRS
  - database per service
  - Kafka integration events and commands, with a transactional outbox (direction) and sagas (direction)
  - logical tenant isolation
- **Current phase:** Phase 1 (foundation) is **complete**. The Phase 1 final audit is done. **Phase 2 (Identity):** Phase 2.1 (provider selection: ASP.NET Core Identity with OpenIddict, [ADR-015](docs/decisions/ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md)) is complete. Phase 2.2 (authentication flows and security design) is **complete and accepted**: [ADR-016](docs/decisions/ADR-016-browser-client-architecture-backend-for-frontend.md) (BFF), [ADR-017](docs/decisions/ADR-017-token-session-and-protocol-strategy.md) (tokens, sessions, protocol), [ADR-018](docs/decisions/ADR-018-account-security-tokens-and-security-email-delivery.md) (account security tokens and email), with rationale, residual risks and the required test plan in [docs/architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md](docs/architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#11-decisions-and-implementation-plan). **Phase 2.3 (Identity implementation) is authorized and in progress**, in small vertical slices. See [docs/architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md](docs/architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md).

## Mandatory reading order

Before implementing any feature or making any architectural change, read:

1. `CLAUDE.md` (this file)
2. [docs/architecture/PROJECT_DECISIONS.md](docs/architecture/PROJECT_DECISIONS.md), the canonical decisions
3. [docs/architecture/SERVICE_CATALOG.md](docs/architecture/SERVICE_CATALOG.md)
4. The relevant ADRs in [docs/decisions/](docs/decisions/README.md)
5. The relevant API contracts (`docs/api/`) and event contracts (`docs/events/`)
6. The relevant service-specific documentation

**If two documents contradict each other, stop and report the inconsistency. Do not guess.**

## Architectural rules

- **Layers:**
  - Domain → nothing
  - Application → Domain
  - Infrastructure → Application, Domain
  - Api → Application, Infrastructure

  Domain and Application never reference EF Core, SQL Server or MongoDB drivers, Kafka, Redis, ASP.NET Core, YARP or Serilog. This is enforced by `tests/ArchitectureTests`. Never weaken those tests to make a change pass.
- **Service ownership:**
  - a service never references another service's projects
  - the Gateway references no service and contains no business logic
- **Data:**
  - database per service, with no cross-service database access, ever
  - store mapping per [ADR-011](docs/decisions/ADR-011-per-service-data-store-assignment.md)
  - Redis is never a system of record
- **Tenancy:**
  - logical isolation ([ADR-014](docs/decisions/ADR-014-logical-tenant-isolation.md))
  - a tenant ID or token claim is **not** authorization proof, so every service authorizes tenant and resource access itself
  - gateway authorization never replaces service authorization
- **Messaging:**
  - domain events stay internal, and integration events are versioned contracts
  - consumers are idempotent
  - no sensitive data in messages
  - no outbox or saga code before its design is approved ([ADR-012](docs/decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md), [ADR-013](docs/decisions/ADR-013-saga-based-workflow-coordination.md))
- **No forced Event Sourcing** ([ADR-010](docs/decisions/ADR-010-no-mandatory-event-sourcing.md)). **Selective CQRS** ([ADR-009](docs/decisions/ADR-009-selective-cqrs.md)).
- **BuildingBlocks:** technical code only, and only when there is a concrete need. Never shared domain models or business entities.
- **No unnecessary abstractions.** No generic repository per table, and no mediator without an approved decision.
- **No new microservices** and **no technology changes** (for example PostgreSQL, RabbitMQ or a new library category) without an architecture review and an ADR.
- **Payments are simulated.** Never collect, store or process raw card data.

## Coding standards

- Nullable reference types and implicit usings are on (via `Directory.Build.props`). Warnings are errors.
- **No unexplained suppression** of compiler or analyzer warnings. A suppression needs a justification comment and a review mention.
- Async methods accept and propagate `CancellationToken` where appropriate.
- Structured logging with Serilog message templates (`LoggerMessage` source generators where practical). Never log secrets, passwords, tokens or personal data unnecessarily.
- Package versions go **only** in `Directory.Packages.props` (Central Package Management). Before adding a package, evaluate its need, maintenance, compatibility and licensing.
- **No hard-coded secrets or connection strings.** Use configuration and environment variables. Local values live in the gitignored `deploy/docker/.env`.
- Validate input at the boundary. Errors use RFC 9457 problem details. Never leak exception details.
- Write meaningful tests: unit tests for domain and application logic, integration tests for API behaviour, and architecture tests for rules. Don't write tests that only inflate counts.
- Follow `.editorconfig` (LF line endings, file-scoped namespaces, `_camelCase` private fields). Namespaces follow project names (`EnterpriseCommerce.{Service}.{Layer}`).

## Git rules

- Never amend, rebase or rewrite existing commits without explicit approval. **Never force-push.**
- Never commit secrets (`.env` stays untracked) or broken code.
- Run the build and tests before every commit. Keep commits focused, with meaningful messages.
- **Commit only when asked.** Don't create a remote or push unless explicitly requested.

## Phase discipline

- Work only on the currently authorized phase. Never start a future phase automatically.
- Don't implement business functionality before its design is approved.
- When a decision is open (see PROJECT_DECISIONS section 13), present alternatives with trade-offs and wait for a decision. **Never decide silently.**
- Major architectural decisions need explicit approval and an ADR before implementation.

## Language rule

**All repository content is in English.** This covers code, identifiers, comments, XML docs, error messages, configuration descriptions, documentation, ADRs, API and event contracts, commit messages, Kafka topics and database objects. Conversation with the user may be in another language, but nothing written to the repository may be.

## Verification report (end of every task)

Report:

- files created
- files modified
- architectural decisions affected
- build result
- test result
- Docker validation result (if relevant)
- Git status
- remaining risks
- deferred work

**Never claim success without running the verification.** If a check couldn't run, say so.

## Commands

```bash
dotnet build EnterpriseCommerce.sln -c Release                      # must be 0 warnings / 0 errors
dotnet test --solution EnterpriseCommerce.sln -c Release            # Microsoft.Testing.Platform (set in global.json)

# SQL-backed integration tests run only when EC_TEST_SQLSERVER is set (they skip otherwise; CI requires them):
export EC_TEST_SQLSERVER='Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true'   # or the Compose sqlserver profile
dotnet tool restore                                                 # dotnet-ef (dotnet-tools.json)
dotnet tool run dotnet-ef migrations add <Name> --project src/Services/Identity/EnterpriseCommerce.Identity.Infrastructure --output-dir Persistence/Migrations --namespace EnterpriseCommerce.Identity.Infrastructure.Persistence.Migrations
dotnet run --project src/Services/Identity/EnterpriseCommerce.Identity.Api --launch-profile https  # https://localhost:7101 (issuer; OpenIddict requires HTTPS)
dotnet run --project src/Gateway/EnterpriseCommerce.Gateway --launch-profile http                  # http://localhost:5000

cd deploy/docker && cp .env.example .env                            # once
docker compose --profile <sqlserver|mongodb|redis|kafka> config --quiet
docker compose --profile <name> up -d --wait
docker compose --profile <name> down                                # same profile flags as for up
```

## Local environment notes

- Windows, Docker Desktop on WSL2. The Docker VM has about 3.8 GB of memory, so **start only the profiles you need**. The `all` profile does not fit as configured.
- A locally installed MongoDB uses port 27017. Use `MONGO_PORT=27018` for the Compose MongoDB.
- In Git Bash, prefix `docker compose exec` commands that pass container paths with `MSYS_NO_PATHCONV=1`.
- SQL-backed tests must also pass against the Compose SQL Server 2022 (`--profile sqlserver`), not only LocalDB: `EC_TEST_SQLSERVER='Server=127.0.0.1,1433;User ID=sa;Password=<from deploy/docker/.env>;TrustServerCertificate=true'`.
- Identity security emails in Development: `docker compose --profile mailsink up -d --wait`, then run a Debug build with `Identity__DevelopmentMailSink__Enabled=true` and read the mail at http://localhost:8025. Never commit that flag.
- Identity needs `ConnectionStrings__IdentityDatabase` (environment or user secrets; never committed). In Development, `Identity__Database__ApplyMigrationsAtStartup=true` applies the migrations at startup.
- `dotnet ef migrations add` puts the model snapshot under a namespace-derived folder when `--namespace` is used. Move it into `Persistence/Migrations` and delete the generated folder.
- The Phase 1 audit findings and open items are in [docs/architecture/PHASE_1_FINAL_AUDIT.md](docs/architecture/PHASE_1_FINAL_AUDIT.md).
