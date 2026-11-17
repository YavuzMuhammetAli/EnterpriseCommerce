# ADR-012: Transactional Outbox and Asynchronous Commands

- **Status:** Accepted (architectural direction; implementation deferred)
- **Date:** 2026-11-17
- **Amends:** [ADR-005](ADR-005-kafka-for-asynchronous-communication.md), which left reliable publishing undecided and described Kafka for integration events only

## Context

[ADR-005](ADR-005-kafka-for-asynchronous-communication.md) chose Kafka for asynchronous integration events. It left the reliable publishing approach to the phase that introduces the first event flow.

The Phase 0 architecture agreement already set two directions:

- the **Transactional Outbox** as the reliable publishing approach, and
- Kafka carrying asynchronous **commands** as well as events, for cross-service workflows ([ADR-013](ADR-013-saga-based-workflow-coordination.md)).

Without an outbox, a service that saves its state and then publishes to Kafka can lose messages (the save succeeds and the publish fails) or publish phantom messages (the publish succeeds and the save rolls back). Both break cross-service consistency.

## Decision

1. **Transactional Outbox is the accepted architectural direction** for publishing integration events and asynchronous commands. A service writes outgoing messages to an outbox in **the same local transaction** as its state change. A separate relay publishes them to Kafka.
2. **Kafka carries both integration events and asynchronous commands.**
   - Integration events state facts that already happened (past tense, for example `OrderConfirmed`). The publisher doesn't know its consumers.
   - Commands ask a specific service to do something (imperative, for example `ReserveStock`). They have one logical owner and recipient, and are used in workflow coordination.
3. Message handling requirements:
   - Consumers are **idempotent**. The outbox gives at-least-once delivery, so duplicates are expected.
   - **Retry with backoff**, and **dead-letter handling** for messages that cannot be processed.
   - **Correlation and causation identifiers** on every message.
   - **Versioned schemas.** Contracts are documented under `docs/events/`.
   - **No sensitive data** in messages (credentials, tokens, payment instrument data).
   - The **tenant context** is carried explicitly where relevant ([ADR-014](ADR-014-logical-tenant-isolation.md)).
4. Domain events stay internal to their owning service.
5. **Deferred, not decided here:**
   - the outbox implementation (table design, relay mechanism, cleanup, ordering guarantees)
   - an inbox or deduplication store
   - the Kafka client library
   - topic naming and partitioning conventions
   - the schema format

   These are decided in the phase that introduces the first event flow. **No outbox code is written before that design is approved.**

## Consequences

- Positive: no lost or phantom messages caused by the gap between the database write and the publish.
- Positive: a uniform, reviewable pattern for every service that publishes.
- Negative: more moving parts (outbox storage, relay process, cleanup) and some publication latency.
- Negative: at-least-once delivery makes consumer idempotency mandatory.
- Negative: the MongoDB-based Catalog needs an outbox variant built on MongoDB transactions or an equivalent. This is addressed in the Catalog design.

## Alternatives considered

- **Publish directly after commit (no outbox):** simple, but can lose messages. Rejected.
- **Distributed transaction spanning the database and Kafka:** not supported across the chosen technologies, and poor for availability. Rejected.
- **Change Data Capture (for example Debezium) instead of an application-level relay:** a valid way to *implement* an outbox relay. It may be evaluated in the implementation design, but it adds infrastructure.
- **Event sourcing as the publishing mechanism:** rejected by [ADR-010](ADR-010-no-mandatory-event-sourcing.md).
- **HTTP for commands:** acceptable for synchronous requests that need an immediate answer. For long-running workflow steps, Kafka commands avoid availability coupling.
