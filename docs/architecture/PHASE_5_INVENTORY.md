# Phase 5: Inventory

- **Status:** in progress (authorized 2026-11-24)
- **Decisions:** [ADR-026](../decisions/ADR-026-inventory-service-design.md) (Inventory design), [ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md) (Catalog variant verification), [ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) (events and the shared outbox block)
- **Contracts:** [INVENTORY_API_CONTRACT.md](../api/INVENTORY_API_CONTRACT.md), [INVENTORY_EVENTS.md](../events/INVENTORY_EVENTS.md)
- **Operations:** [INVENTORY_OPERATIONS.md](../operations/INVENTORY_OPERATIONS.md)

## 1. Inputs and scope

**In scope:**

- the Inventory service: four layers under `src/Services/Inventory`, with its own SQL Server database
- merchant stock management
- the reservation lifecycle for future checkout orchestration, through internal endpoints
- reservation expiry
- integration events through the transactional outbox
- Catalog variant verification (ADR-027)
- Gateway and Compose integration
- the extraction of `BuildingBlocks.Messaging` (ADR-028 section 4)

**Out of scope:**

- Cart, Order, Payment and Fulfillment
- any placeholder Order
- saga code (ADR-013 rule 9)
- consumers and inboxes
- Catalog events
- Redis

## 2. Milestones and progress

| # | Milestone | State |
|---|---|---|
| 1 | Design decisions (ADR-026 to ADR-028) | done |
| 2 | `BuildingBlocks.Messaging`; Tenant & Merchant moves to it | done |
| 3 | Service scaffold and domain | done |
| 4 | SQL Server persistence, migration, outbox mapping and event contracts | done |
| 5 | Catalog variant verification endpoint | planned |
| 6 | Merchant stock management with membership and Catalog verification | planned |
| 7 | Reservation lifecycle (internal API) | planned |
| 8 | Reservation expiry worker | planned |
| 9 | Kafka relay, Gateway route, topic and development keys | planned |
| 10 | Measurements, evidence and closure | planned |
