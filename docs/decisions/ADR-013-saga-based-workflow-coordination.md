# ADR-013: Saga-Based Workflow Coordination

- **Status:** Accepted (architectural direction; detailed design deferred)
- **Date:** 2026-11-17

## Context

Database per service ([ADR-003](ADR-003-database-per-service.md)) rules out distributed transactions. Checkout spans Order, Inventory and Payment, and then triggers Fulfillment and Notification. These steps must either all succeed or be compensated, and customers must never be told an order is confirmed when it is not.

The Phase 0 architecture agreement chose sagas for these workflows and defined the order states and checkout flow.

## Decision

1. **Cross-service business workflows are coordinated as sagas.** Each workflow uses **orchestration or choreography, whichever is appropriate**:
   - **Orchestration** (a coordinator that sends commands and reacts to replies) for workflows with several steps, explicit compensation and a business-visible state. **Checkout is expected to be orchestrated by the Order service**, which owns checkout coordination.
   - **Choreography** (services reacting to each other's events) for simple, loosely coupled reactions, such as notifications after an order is confirmed.
2. Every saga step is a **local transaction** in the owning service. Its outgoing messages are published through the transactional outbox ([ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md)).
3. **Checkout states are distinct and explicit:** *pending (draft)*, *confirmed*, *checkout failed* and *cancelled*.
4. **`OrderConfirmed` is published only after the checkout workflow has completed successfully.** That means stock is reserved and payment authorization (simulated) has succeeded.
5. **Order snapshots are immutable.** At checkout, Order stores the product name, SKU, unit price, currency and quantity. Later Catalog price changes never modify historical orders.
6. **Checkout compensation** (undoing the steps of a failed checkout) and **cancellation of an already confirmed order** are **separate workflows**.
7. **Reservation expiration** is an explicit mechanism in Inventory that publishes a corresponding event. Expired reservations must not leave orders waiting forever.
8. **MVP scope is single-merchant checkout.** Multi-merchant checkout and merchant-specific fulfillment groups are advanced scope.
9. **Deferred, not decided here:**
   - the detailed saga state machine, timeouts and retry policies
   - the reservation expiration design
   - whether a saga library is used
   - advanced saga recovery

   These are designed in the checkout phase, and **no saga code is written before that design is approved**.

## Consequences

- Positive: consistent outcomes across services without distributed transactions, and an explicit, observable checkout state.
- Positive: orchestrating checkout keeps its logic in one place, the Order service, which simplifies reasoning and testing.
- Negative: eventual consistency. The customer sees a *pending* state before confirmation, and the UI must handle it.
- Negative: compensation logic, timeouts and idempotency add design and testing effort.
- Negative: an orchestrator can turn into a "god service" if it absorbs other services' rules. It must only coordinate, and each participant keeps its own business rules.

## Alternatives considered

- **Distributed transactions (2PC):** not supported across the chosen stores and broker, and harmful for availability. Rejected.
- **Choreography only:** low coupling, but checkout's flow and compensation would be spread across services and hard to follow. Rejected for checkout, and kept for simple reactions.
- **Synchronous HTTP chain during checkout:** couples availability of all participants and makes compensation after a partial failure fragile. Rejected.
- **Confirming the order before payment and reserving afterwards:** risks confirming orders that cannot be fulfilled. Rejected by rule 4.
