# Integration Events

Integration event contracts go here, one document per event or per topic, once the first event flow is designed ([ADR-005](../decisions/ADR-005-kafka-for-asynchronous-communication.md)).

There are no integration events or topics yet. Kafka runs locally in KRaft mode with automatic topic creation disabled.

Tenant & Merchant (Phase 3 S1) deliberately publishes no event and contains no outbox code, in line with ADR-012. Its first event (provisionally `TenantProvisioned`) is designed with the outbox in the first event-flow phase ([PHASE_3_TENANT_MERCHANT.md section 7](../architecture/PHASE_3_TENANT_MERCHANT.md#7-integration-events)).

Accepted rules for every contract documented here ([ADR-012](../decisions/ADR-012-transactional-outbox-and-asynchronous-commands.md), [ADR-013](../decisions/ADR-013-saga-based-workflow-coordination.md), [ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)):

- Integration events state facts in the past tense. Commands are imperative and have a single owner.
- Contracts are versioned, and consumers are idempotent.
- Every message carries correlation and causation identifiers, and tenant context where relevant.
- Messages never contain sensitive data (credentials, tokens or payment instrument data).
- Messages are published through the transactional outbox. Its implementation is designed in the first event-flow phase.

Planned topics: event naming and envelope, topic naming and ownership, partition keys, versioning and compatibility rules, idempotency expectations for consumers, and the reliable publishing approach.
