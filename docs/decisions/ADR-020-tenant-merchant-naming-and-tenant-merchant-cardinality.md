# ADR-020: Tenant & Merchant Naming and Tenant-Merchant Cardinality

- **Status:** Accepted
- **Date:** 2026-11-21
- **Phase:** 3 (Tenant & Merchant), slice S1
- **Resolves:** "Tenant and merchant naming conventions, including whether `TenantMerchant` is the right project name" in [PROJECT_DECISIONS.md section 13](../architecture/PROJECT_DECISIONS.md#13-explicitly-deferred-decisions), the last open tenancy item of [ADR-014](ADR-014-logical-tenant-isolation.md).
- **Related:** [ADR-014](ADR-014-logical-tenant-isolation.md) (logical tenant isolation), [ADR-017](ADR-017-token-session-and-protocol-strategy.md) (audiences, scopes and claims), [ADR-011](ADR-011-per-service-data-store-assignment.md) (SQL Server). Design and implementation: [PHASE_3_TENANT_MERCHANT.md](../architecture/PHASE_3_TENANT_MERCHANT.md).

## Context

ADR-014 settled the tenancy model: logical isolation, Tenant & Merchant as the owner of the tenant and merchant lifecycle, and service-enforced authorization. It left only the naming open, including whether `TenantMerchant` is the right project name.

Building the first slice of the service needed two more things that no accepted document defines:

- **A name** for the folder, the projects, the namespaces, the database, the token audience and the service scopes.
- **How tenants relate to merchants.** The accepted documents use both words. PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.1.2 describes onboarding as creating "the merchant" and recording "the owner membership", and invitations as "(email, merchant, role)". ADR-014 makes the tenant the isolation boundary that every tenant-owned record carries. Whether a tenant contains several merchants, or is one merchant, decides the schema, the membership scope and every other service's tenant checks.

The original Phase 0.2 database design is not in the repository, so it cannot answer either question.

## Decision

1. **Name: `TenantMerchant`.** The provisional name is confirmed. It applies to:

   | Item | Value |
   |---|---|
   | Service folder | `src/Services/TenantMerchant` |
   | Projects and namespaces | `EnterpriseCommerce.TenantMerchant.{Domain,Application,Infrastructure,Api}` |
   | Connection string | `ConnectionStrings:TenantMerchantDatabase` |
   | Service token audience | `tenant-merchant-api` |
   | Service scope prefix | `svc.tenant-merchant.` (for example `svc.tenant-merchant.provisioning`) |
   | Configuration section | `TenantMerchant` |

   In prose the service stays "Tenant & Merchant".

2. **A tenant is one merchant organization (1:1).**
   - The tenant is the ADR-014 isolation boundary. Its merchant profile describes the merchant that the marketplace shows.
   - A tenant has exactly one merchant profile, created in the same transaction as the tenant.
   - **Memberships and merchant roles are tenant-scoped.** "Member of the merchant" and "member of the tenant" mean the same thing.
   - Other services scope merchant-owned data (products, stock, orders) by the tenant ID.

3. **Not decided here:** the merchant role catalog and permissions, the membership state machine, the slug rules and the provisioning contract. These are design details within this ADR and ADR-014, recorded in PHASE_3_TENANT_MERCHANT.md.

## Consequences

- Positive: one identifier, the tenant ID, scopes every merchant-owned record in every service. There is no second merchant ID to keep consistent with it.
- Positive: authorization stays simple. A membership in the tenant is a membership in the merchant.
- Positive: the open naming item of PROJECT_DECISIONS section 13 is closed, and the names follow the existing conventions (`identity-api`, `svc.identity.*`).
- Negative: a business that wants several separately branded merchants needs several tenants, with separate memberships. Supporting several merchants inside one tenant later would need a new ADR and a migration that adds a merchant identifier to merchant-owned data.
- Negative: `TenantMerchant` is a compound name. It matches the service's two responsibilities, and is kept for clarity over brevity.

## Alternatives considered

- **A tenant contains many merchants:** flexible, for example for a white-label operator hosting several shops. It needs a merchant ID next to the tenant ID on all merchant-owned data, membership per merchant, and a two-level authorization check in every service. No accepted requirement asks for it. Rejected for now.
- **Tenant only, merchant profile added later:** a smaller S1, but the merchant profile is part of onboarding (PROJECT_DECISIONS section 5), and adding it later changes the provisioning contract. Rejected.
- **The names `Tenancy` or `Merchant`:** shorter. `Tenancy` hides the merchant lifecycle, and `Merchant` hides that the service is the tenancy authority (ADR-014). Rejected.
