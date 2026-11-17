# ADR-009: Selective CQRS

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

Command Query Responsibility Segregation (CQRS) separates state-changing operations from reads. It helps where write models enforce rich invariants while reads need differently shaped, optimized views (for example, catalog search or order history). Applied everywhere, it adds handlers, models and indirection to simple operations that gain nothing from them.

## Decision

Apply CQRS **selectively, per use case**.

- In the Application layer, a use case may be modelled as a command (changes state, enforces invariants through the domain model) or a query (reads data, may bypass the domain model and use projections).
- Separate read models or read stores are introduced **only** when a concrete need (for example, query shape or performance) is shown in a service's design.
- Simple CRUD-like operations may use straightforward application services without command/query objects.
- One logical database per service remains the default. CQRS does not imply separate databases.
- **No mediator or CQRS library is mandated by this ADR.** Introducing one (for example, MediatR or an alternative) requires a separate evaluation of need, maintenance status and licensing. Note that MediatR moved to a commercial licence model for recent versions.

## Consequences

- Positive: complexity is added where it pays off and avoided where it doesn't.
- Positive: queries can be optimized (projections, no change tracking) without bending the domain model.
- Negative: there is no uniform pattern across all use cases, so reviewers must check that the choice fits each case.
- Negative: where separate read models are introduced, they bring eventual consistency between write and read sides.

## Alternatives considered

- **CQRS everywhere (with a mediator pipeline for every operation):** uniform, but adds ceremony and indirection for simple operations. Rejected as a mandate.
- **No CQRS (single model for reads and writes):** simpler, but forces read-optimized needs through the write model. Rejected as a blanket rule.
- **Full CQRS with separate read databases by default:** high infrastructure and consistency cost without a demonstrated need. Rejected.
