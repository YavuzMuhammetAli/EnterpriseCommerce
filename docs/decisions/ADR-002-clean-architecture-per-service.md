# ADR-002: Clean Architecture per Service

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

Each microservice contains business rules that should be testable without databases, brokers or HTTP, and infrastructure choices that may change independently of those rules. Without an explicit internal structure, services tend to mix persistence, transport and business logic.

At the same time, applying every Clean Architecture and DDD pattern uniformly (generic repositories, a mediator for every call, a value object for every primitive) adds ceremony without value, especially for simple services.

## Decision

Each service follows a **selective Clean Architecture** with four projects:

- **Domain:** entities, aggregates, value objects, domain events, business rules and domain exceptions. No dependency on other layers or on any infrastructure technology.
- **Application:** use cases, commands and queries where appropriate, handlers, application interfaces, validation and orchestration. Depends only on Domain.
- **Infrastructure:** EF Core / MongoDB persistence, configurations, repository implementations, Kafka/Redis integrations and external adapters. Implements Application interfaces and may reference Domain.
- **Api:** endpoints, request and response models, middleware, DI composition, authentication and authorization configuration, and OpenAPI. It is the composition root and references Application and Infrastructure.

Patterns are applied **selectively**: DDD tactical patterns where the domain is rich, and simple code where it isn't. There are no generic repositories for every table. A service's projects never reference another service's projects. These rules are enforced by `EnterpriseCommerce.ArchitectureTests` (see [SOLUTION_STRUCTURE.md](../architecture/SOLUTION_STRUCTURE.md)).

## Consequences

- Positive: business rules can be unit-tested in isolation, and infrastructure can be replaced without touching the domain.
- Positive: a consistent structure across ten services lowers the cost of moving between them.
- Positive: automated dependency checks stop gradual erosion of the boundaries.
- Negative: four projects per service add some overhead, which is felt most in simple services.
- Negative: the "selective" rule needs judgement. Reviewers must resist both over-engineering and shortcuts.

## Alternatives considered

- **Vertical slice architecture only:** organizes code by feature with minimal layering. It is attractive for simple services, and slices can still be used *inside* the Application layer. It was rejected as the sole structure because the enforced dependency direction is valuable for keeping infrastructure out of business rules.
- **Single project per service with folders:** less overhead, but layer rules can only be enforced by convention. Rejected.
- **Hexagonal / ports-and-adapters naming:** conceptually equivalent. The Domain/Application/Infrastructure/Api naming was chosen because it is familiar in the .NET ecosystem.
