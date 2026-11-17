# ADR-007: YARP API Gateway

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

Clients should not need to know the address of each of the ten services. Edge concerns such as routing, TLS termination, rate limiting, request size limits, correlation and token validation should be handled consistently in one place. A gateway is also a common place for business logic to accumulate, which would recreate a coupled monolith at the edge.

## Decision

Use **YARP (Yet Another Reverse Proxy)** in a dedicated ASP.NET Core project, `EnterpriseCommerce.Gateway`, as the single external entry point.

- The gateway is an **infrastructure component, not a business service**.
- Routes and clusters are **externalized in configuration** (`ReverseProxy` section). Environment-specific addresses are set through configuration files or environment variables.
- The gateway may apply edge concerns as they are introduced. It may *validate* tokens, but authentication flows and token issuance belong to Identity.
- The gateway must **not** contain business logic, aggregate or transform business data across services, or reference any service project (enforced by architecture tests).
- The gateway exposes its own `/health` endpoint, which is never proxied.

Phase 1 provides a minimal gateway, with an example `/identity/{**catch-all}` route to the Identity API that is configured for Development.

## Consequences

- Positive: YARP is a .NET library maintained by Microsoft and configured with standard ASP.NET Core mechanisms (configuration, middleware, DI, logging, OpenTelemetry), so it shares the same toolchain as the services.
- Positive: highly customizable in C# when needed, while remaining configuration-driven for routing.
- Negative: it is another hop and another deployable to operate, and it can become a bottleneck or single point of failure without scaling.
- Negative: it is a library rather than a turnkey API management product. Features like a developer portal or monetization aren't included (not needed).

## Alternatives considered

- **Ocelot:** a .NET API gateway with built-in features. It has a slower release cadence and is less flexible than building on ASP.NET Core directly.
- **NGINX / Envoy / Traefik:** mature, high-performance proxies, but non-.NET configuration and extensibility, and a separate toolchain.
- **Kong / commercial API management (Azure API Management, AWS API Gateway):** rich feature sets, but heavier, either cloud-coupled or a separate platform. They could front the system in a production deployment.
- **No gateway (clients call services directly):** exposes internal topology and duplicates edge concerns. Rejected.
