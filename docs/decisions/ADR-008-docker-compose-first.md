# ADR-008: Docker Compose First

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

Developing ten services requires SQL Server, MongoDB, Redis and Kafka locally, with observability tools to follow. The development machine has 16 GB of RAM. The environment must be reproducible, quick to start, and selective, so that a developer runs only what the current task needs.

## Decision

Use **Docker and Docker Compose** as the first-class local environment, defined in `deploy/docker/compose.yaml`.

- **Profiles** control what runs (`sqlserver`, `mongodb`, `redis`, `kafka`, `all`). Nothing starts by default.
- **Pinned image versions**, never `latest`. Upgrades are explicit changes.
- **Named volumes** for persistence, and **health checks** on every service.
- **Memory limits** sized for a 16 GB machine.
- **Configuration through environment variables** from a gitignored `.env`. A committed `.env.example` holds documented, local-only placeholder credentials. Real credentials are never committed.
- Ports bind to `127.0.0.1` only.
- **Application containers are added only once they run reliably** with their dependencies. In Phase 1, the Identity API and the Gateway run on the host with `dotnet run`.
- The observability stack (OpenTelemetry Collector, Prometheus, Grafana, Loki) is added in a later phase as additional profiles.

## Consequences

- Positive: one command per profile gives a reproducible environment. Onboarding needs only Docker and the .NET SDK.
- Positive: profiles keep memory use proportional to the task.
- Positive: the same Compose file can be validated in CI.
- Negative: Compose is not a production orchestrator. Production deployment (for example, Kubernetes) needs separate manifests in a later phase.
- Negative: on Windows, Docker Desktop and WSL2 add memory overhead and require the daemon to be running.

## Alternatives considered

- **Kubernetes locally (kind, minikube, k3d):** closer to production, but much heavier and slower for day-to-day development. Deferred to a deployment phase.
- **.NET Aspire AppHost:** strong developer experience for .NET distributed apps. It is not part of the agreed stack. It could be evaluated later as an orchestration layer without replacing the Compose definitions.
- **Locally installed databases and brokers:** not reproducible, version drift between developers, and hard to reset. Rejected.
