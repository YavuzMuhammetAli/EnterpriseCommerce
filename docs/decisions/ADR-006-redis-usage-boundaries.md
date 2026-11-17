# ADR-006: Redis Usage Boundaries

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

Redis is part of the technology stack. Its speed makes it tempting to use for everything, including as a primary database or as a shared data channel between services. Both would undermine data ownership ([ADR-003](ADR-003-database-per-service.md)) and durability guarantees. Clear boundaries are needed before the first service adopts it.

## Decision

Redis may be used for **ephemeral and derived data** only:

- caching of data whose source of truth lives elsewhere,
- short-lived technical state (for example, rate-limiting counters, idempotency keys with a TTL, or short-lived locks where the design documents their failure modes),
- other ephemeral data explicitly justified in a service's design.

Boundaries:

1. **Redis is never the system of record.** Losing Redis data must never lose business data. Any data stored in Redis can be rebuilt or safely expire.
2. **No cross-service data sharing through Redis.** A service's keys are private to it (key prefix per service, and separate logical databases or instances when deployed). Integration goes through APIs or Kafka ([ADR-005](ADR-005-kafka-for-asynchronous-communication.md)).
3. Every key has a defined TTL or a documented reason for not having one.
4. **Per-service Redis usage is not decided by this ADR**, including whether the Cart service uses Redis for any of its state. That is an open decision for each service's design phase, made within these boundaries.
5. Redis client code lives only in Infrastructure layers.

Local development uses a pinned `redis` 8.x container with a password, a 256 MB memory cap and the `allkeys-lru` eviction policy, which fits the cache use case.

## Consequences

- Positive: services stay correct even if Redis is flushed or unavailable (possibly slower, but not wrong).
- Positive: no hidden coupling between services through shared keys.
- Negative: services that might benefit from Redis as a primary store must justify that against rule 1 in their design phase.
- Note on licensing: Redis 8 is distributed under a choice of RSALv2, SSPLv1 or AGPLv3. Using the unmodified Redis server as an internal component (locally or self-hosted) is compatible with this project. The licence must be re-evaluated before offering Redis as a service or redistributing modified versions. A managed offering or a BSD-licensed fork (Valkey) remains an option at deployment time.

## Alternatives considered

- **In-process memory cache only:** fine for single-instance services, but inconsistent across replicas. It can complement Redis per service.
- **Valkey (BSD-licensed Redis fork):** protocol-compatible and a viable drop-in replacement. Redis was kept as the agreed stack choice, with Valkey noted as an option if licensing requirements change.
- **Redis as a primary store for selected services:** not permitted by default. A service design may propose it through a new ADR that supersedes rule 1 for that service.
