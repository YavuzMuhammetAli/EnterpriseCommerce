# ADR-005: Kafka for Asynchronous Communication

- **Status:** Accepted. Amended by [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md): the transactional outbox is the accepted reliable publishing direction, and Kafka also carries asynchronous commands. Its deferred items (the client library and reliable publishing) are resolved by [ADR-023](ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md) (2026-11-23).
- **Date:** 2026-11-17

## Context

Services own their data ([ADR-003](ADR-003-database-per-service.md)) and must react to each other's state changes. For example, Notification reacts to order placement, and read models are built from upstream changes. Synchronous HTTP chains for these flows would couple availability and latency across services. The platform needs a durable, scalable broker for integration events.

## Decision

Use **Apache Kafka** as the asynchronous integration backbone, running in **KRaft mode** (no ZooKeeper).

- Services publish **integration events** describing facts that already happened in their own domain. Domain events stay internal to a service. Integration events are its public, versioned contract.
- Consumers must be **idempotent** and must tolerate redelivery and out-of-order events where relevant.
- Topics are created explicitly. Automatic topic creation is disabled.
- Event contracts are documented under `docs/events/` when the first event is introduced.
- Synchronous HTTP stays appropriate for queries and commands that need an immediate answer.
- Kafka client code lives only in Infrastructure layers.

Reliable publishing (for example, a transactional outbox) and the client library choice are **not decided in this ADR**. They are designed in the phase that introduces the first event flow. Phase 1 provides only a local single-node Kafka container (`apache/kafka`, pinned version).

## Consequences

- Positive: temporal decoupling. A temporarily unavailable consumer doesn't block the producer.
- Positive: durable, replayable event log, which supports rebuilding read models and adding new consumers later.
- Negative: eventual consistency must be handled explicitly in the business flows and in the UI.
- Negative: operational complexity covers partitions, consumer groups, offsets, retention and schema evolution.
- Negative: noticeable local resource use (a JVM). This is mitigated by a capped heap and an opt-in Compose profile.

## Alternatives considered

- **RabbitMQ:** strong for task queues and routing, and simpler to operate. It was rejected as the backbone because the project values a durable, replayable log and Kafka's partitioned consumer model.
- **Azure Service Bus / AWS SNS+SQS:** managed and robust, but they tie local development and the architecture to one cloud provider.
- **Synchronous HTTP only:** creates availability coupling and cascading failures. Rejected for integration flows.
- **Kafka with ZooKeeper:** ZooKeeper was removed in Kafka 4.x, so KRaft is the supported mode.
