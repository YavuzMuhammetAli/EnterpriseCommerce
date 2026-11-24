# BuildingBlocks

Shared **technical** libraries for services and the Gateway. Empty by design in Phase 1. Current blocks:

- `EnterpriseCommerce.BuildingBlocks.Authentication`: resource-server token validation (Phase 4, [ADR-025](../../docs/decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md) section 3), for `*.Api` projects only.
- `EnterpriseCommerce.BuildingBlocks.Messaging`: the SQL Server transactional outbox and its Kafka relay (Phase 5, [ADR-028](../../docs/decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) section 4), for `*.Infrastructure` projects only.

A building block is added only when a concrete need exists, such as the same technical code needed by at least two services. The full policy is in [SOLUTION_STRUCTURE.md](../../docs/architecture/SOLUTION_STRUCTURE.md#buildingblocks-policy). In short:

- Allowed: technical concerns (for example, observability setup, health-check conventions, messaging plumbing, common middleware).
- Not allowed: shared domain models, shared business entities, or shared contracts that couple services.
- Domain projects never reference BuildingBlocks.
- Naming: `EnterpriseCommerce.BuildingBlocks.{Concern}`, placed in `src/BuildingBlocks/EnterpriseCommerce.BuildingBlocks.{Concern}/`.
