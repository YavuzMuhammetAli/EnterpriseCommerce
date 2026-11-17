# ADR-014: Logical Tenant Isolation

- **Status:** Accepted
- **Date:** 2026-11-17

## Context

EnterpriseCommerce is multi-tenant. Data of different tenants and merchants lives in the same service databases, so the platform must make sure one tenant can never read or change another tenant's data.

Three common approaches are separate infrastructure per tenant, separate databases per tenant, and logical isolation in shared databases. The Phase 0 architecture agreement chose logical isolation.

A common security mistake in multi-tenant systems is to trust a tenant identifier or a role claim in a token as proof of access. That allows cross-tenant access whenever the identifier is wrong, stale or manipulated.

## Decision

1. **Logical tenant isolation.** Tenants share each service's database, and every tenant-owned record carries an explicit tenant identifier. There is **no database per tenant** unless a later ADR changes this decision.
2. **Tenant identification is explicit**, and the tenant context of every request, message and background job is validated before use.
3. **Tenant ownership and membership are managed by Tenant & Merchant.** Other services may keep local read models of the membership data they need, built from its integration events ([ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md)).
4. **A tenant ID or token claim alone is not authorization proof.** Every service performs **tenant-level and resource-level authorization** for the resources it owns. It checks that the caller is allowed to act on *that* resource in *that* tenant.
5. **Gateway authorization does not replace service authorization** ([ADR-007](ADR-007-yarp-api-gateway.md)).
6. **Cross-tenant access is prevented by default** in queries, commands, integration events and background jobs. For example, every tenant-owned query is scoped to the tenant, and every message carries the tenant context where relevant.
7. Platform administration (Admin) and merchant-level authorization (Tenant & Merchant) are **separate concerns**. Platform-level access to tenant data is explicit, authorized and audited.
8. **Scope of this decision.** The initial tenancy model is **settled by this ADR and is not an open decision**:
   - logical isolation, with no database per tenant
   - Tenant & Merchant owns the tenant and merchant lifecycle
   - every service enforces tenant-level and resource-level authorization
   - a tenant ID or token claim is never sufficient proof

   **Open:** only the exact naming conventions, including whether `TenantMerchant` is the right project name. Later phases work out **design-level details within this model**, and those details must conform to this ADR:
   - the shape of tenant context in tokens (Phase 2 token strategy)
   - the membership data shared with other services (Tenant & Merchant design)
   - the enforcement technique in each service (for example, EF Core global query filters versus explicit scoping)

## Consequences

- Positive: efficient resource use and simple operations, with one database per service regardless of tenant count.
- Positive: authorization is enforced where the data lives, so a mistake at the edge does not expose data.
- Negative: isolation depends on correct application code in every service. A missing tenant filter is a data leak. This must be backed by conventions, reviews and tests (including cross-tenant negative tests).
- Negative: noisy-neighbour effects are possible, because tenants share database resources.
- Negative: per-tenant backup, restore or data residency is harder than with database-per-tenant.

## Alternatives considered

- **Database per tenant:** strong isolation and easy per-tenant restore, but high operational cost (migrations across many databases, connection management) for a marketplace with many small merchants. Rejected for now. A later ADR may introduce it for specific needs.
- **Schema per tenant:** intermediate isolation, with similar migration overhead and weaker tooling support. Rejected.
- **Trusting tenant claims at the gateway only:** simple, but a single bypass or bug exposes all tenants. Rejected by rules 4 and 5.
