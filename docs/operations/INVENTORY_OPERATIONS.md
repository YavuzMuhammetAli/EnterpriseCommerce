# Inventory Operations

The operational runbook of the Inventory service: local start, configuration, migrations, health, metrics, logs, the event relay, the reservation expiry, and failure handling.

- **Decisions:** [ADR-026](../decisions/ADR-026-inventory-service-design.md), [ADR-027](../decisions/ADR-027-catalog-variant-verification-for-other-services.md), [ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md)
- **Contracts:** [INVENTORY_API_CONTRACT.md](../api/INVENTORY_API_CONTRACT.md), [INVENTORY_EVENTS.md](../events/INVENTORY_EVENTS.md)
- **Code:** `src/Services/Inventory`. The outbox relay is in `src/BuildingBlocks/EnterpriseCommerce.BuildingBlocks.Messaging`, and its behaviour is the same as Tenant & Merchant's ([TENANT_MERCHANT_EVENT_RELAY.md](TENANT_MERCHANT_EVENT_RELAY.md)).

## 1. Local start

1. **Start the infrastructure.** `docker compose --profile sqlserver --profile kafka up -d --wait`, then `docker compose --profile kafka wait kafka-init`.
   - On a broker started before Phase 5, run `docker compose --profile kafka run --rm kafka-init` once to create `inventory.events`.
2. **Create the development keys** (outside the repository): `dotnet run deploy/dev/bff-dev-keys.cs`. Then load the generated scripts:
   - `identity-clients.ps1` before Identity: it registers `svc-inventory` with the scopes `svc.tenant-merchant.membership-verification` and `svc.catalog.variant-verification`.
   - `tenant-merchant-identity.ps1` before Tenant & Merchant: it allow-lists `svc-inventory` for membership verification.
   - `catalog-tenant-merchant.ps1` before Catalog: it allow-lists `svc-inventory` for variant verification.
   - `inventory-services.ps1` before Inventory.
3. **Start Inventory:**

   ```powershell
   $env:ConnectionStrings__InventoryDatabase = 'Server=127.0.0.1,1433;Database=EnterpriseCommerce_Inventory;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=true'
   $env:Inventory__Database__ApplyMigrationsAtStartup = 'true'
   $env:Inventory__Kafka__BootstrapServers = '127.0.0.1:9094'
   dotnet run --project src/Services/Inventory/EnterpriseCommerce.Inventory.Api --launch-profile https   # https://localhost:7104
   ```

**Without the integrations** (Development only), the service starts:

- every merchant request answers `503 membership-unavailable`
- item creation answers `503 catalog-unavailable`
- without Kafka the relay is disabled, and events stay pending in `OutboxMessages`

**The reservation API** has no allowed client by default. Order does not exist. To experiment, register an `svc-` client at Identity with the scope `svc.inventory.reservations` and the audience `inventory-api`, and list it in `Inventory__Reservations__AllowedClientIds__0`. Never commit such a client.

## 2. Configuration

| Setting | Default | Allowed and meaning |
|---|---|---|
| `ConnectionStrings:InventoryDatabase` | none | Required. Environment or user secrets, never committed. |
| `Inventory:Database:ApplyMigrationsAtStartup` | `false` | Development only. Deployed environments migrate as a separate step (section 5). |
| `Inventory:Authentication:Issuer` | none | Required, matched exactly against `iss`; HTTPS outside Development |
| `Inventory:Authentication:ServiceAudience`, `UserAudiences` | `inventory-api`, `[ec-merchant]` | The accepted token audiences |
| `Inventory:ServiceClient:TokenEndpoint`, `Issuer`, `ClientId`, `PrivateKeyPath`, `KeyId`, `Timeout` | –, the authentication issuer, `svc-inventory`, –, –, 5 s | The `private_key_jwt` client at Identity. Required outside Development; a partial configuration is an error. The key is a file outside the repository. |
| `Inventory:MembershipVerification:BaseAddress`, `Timeout` | –, 5 s | Tenant & Merchant's internal base address. Required outside Development. |
| `Inventory:CatalogVerification:BaseAddress`, `Timeout` | –, 5 s | Catalog's internal base address. Required outside Development. |
| `Inventory:Reservations:AllowedClientIds` | empty | `svc-` clients allowed to reserve |
| `Inventory:Reservations:DefaultLifetime`, `MaxLifetime` | 15 min, 1 h | Each 1 minute to 24 hours; the default at most the maximum |
| `Inventory:ReservationExpiry:Enabled`, `Interval`, `BatchSize`, `OverdueAfter` | `true`, 5 s, 50, 2 min | Interval 1 s to 5 min, BatchSize 1 to 500, OverdueAfter 10 s to 1 h |
| `Inventory:Outbox:*` | topic `inventory.events`, poll 1 s, batch 100, max backoff 60 s, halt after 10, retention 7 d, cleanup 1 h, stale after 5 min | As in [TENANT_MERCHANT_EVENT_RELAY.md section 2](TENANT_MERCHANT_EVENT_RELAY.md) |
| `Inventory:Kafka:BootstrapServers`, `SecurityProtocol`, `Ssl*Location`, `MessageTimeout` | –, `Plaintext`, –, 30 s | Required outside Development, with mutual TLS (`Ssl` and a client certificate and key) |

Invalid settings stop the service at startup (`ValidateOnStart`). The error names the section and this document.

**Peer services:**

- Tenant & Merchant must list `svc-inventory` in `TenantMerchant:MembershipVerification:AllowedClientIds`.
- Catalog must list it in `Catalog:VariantVerification:AllowedClientIds`.
- Identity must register the client with both scopes.

## 3. Health

| Endpoint | Checks | Meaning |
|---|---|---|
| `/health`, `/health/live` | none | The process is alive. No dependency outage fails it. |
| `/health/ready` | the database is reachable and has no pending migration | `Unhealthy` (`503`) takes the instance out of rotation |
| `/health/outbox` | the relay ([TENANT_MERCHANT_EVENT_RELAY.md section 3](TENANT_MERCHANT_EVENT_RELAY.md)) | `Degraded` when the oldest pending row is older than `StaleAfter`, or when the leader is backing off, halted or without its topic. Never part of readiness. |
| `/health/expiry` | overdue active reservations, from the database; the local worker's last cycle | `Degraded` when a reservation stays overdue beyond `OverdueAfter`, or when the enabled worker has not completed a cycle within 3 intervals plus `OverdueAfter`. Never part of readiness: commit and release expire overdue reservations themselves. |

Responses carry the status word only. `Degraded` answers `200`.

## 4. Metrics and logs

**Meters.** No exporter is wired yet (observability phase). Tags are bounded closed sets only: never a tenant, item, variant, SKU, reservation, subject or client.

| Meter | Instrument | Tags |
|---|---|---|
| `EnterpriseCommerce.Inventory` | `inventory.membership_checks` | `outcome`: `allowed`, `not_visible`, `forbidden`, `unavailable` |
| | `inventory.catalog_verifications` | `outcome`: `eligible`, `not_found`, `not_eligible`, `unavailable` |
| | `inventory.adjustments` | `outcome`: `applied`, `replayed`, `refused` |
| | `inventory.reservations` | `outcome`: `created`, `replayed`, `insufficient_stock`, `unknown_variant`, `refused` |
| | `inventory.reservation_transitions` | `transition`: `commit`, `release`; `outcome`: `changed`, `unchanged`, `expired_now`, `conflict` |
| | `inventory.reservations_expired` | `by`: `worker`, `lazy` |
| | `inventory.expiry_cycles` | `outcome`: `completed`, `failed` |
| `EnterpriseCommerce.Inventory.Outbox` | the relay's instruments (`outbox.pending`, `outbox.oldest_pending_age_seconds`, `outbox.published`, `outbox.publish_failures`, `outbox.halts`, `outbox.leader`, `outbox.halted`, durations) | `category` only |

**Alert candidates:**

- `/health/expiry` `Degraded` for more than a few minutes
- `outbox.halted` = 1
- `outbox.oldest_pending_age_seconds` above 5 minutes
- a rising `inventory.membership_checks{outcome=unavailable}`

**Logs** are Serilog compact JSON with the `Service` property `Inventory` and the request's `CorrelationId`. Request logs carry the path only, never the query string. No token, key or request body is ever logged.

| Event IDs | Source |
|---|---|
| 5000 to 5002 | Unhandled exception; the database or a dependency unavailable (503) |
| 5020, 5021 | A deadlock retried; retries exhausted |
| 5100 to 5102 | Access denied (no membership, insufficient role); an unknown role from Tenant & Merchant |
| 5200, 5210 | No verdict from Tenant & Merchant or Catalog (failure kind and HTTP status only) |
| 5300 to 5302 | Reservations expired; the worker disabled; an expiry cycle failed |
| 3300 to 3349 | The shared outbox relay (BuildingBlocks.Messaging), as for Tenant & Merchant |

## 5. Migrations

- **Migrations:** `InitialInventorySchema` (the tables, constraints, indexes and triggers) and `InventoryListingOrder` (the listing index on creation time).
- **Generate** with `dotnet tool run dotnet-ef migrations add <Name> --project src/Services/Inventory/EnterpriseCommerce.Inventory.Infrastructure --startup-project src/Services/Inventory/EnterpriseCommerce.Inventory.Infrastructure --output-dir Persistence/Migrations --namespace EnterpriseCommerce.Inventory.Infrastructure.Persistence.Migrations`. If EF creates a namespace-derived folder for the snapshot, move the snapshot into `Persistence/Migrations`.
- **Apply in a deployed environment** as a separate step, before the new build starts: `dotnet ef database update` with `EC_INVENTORY_DESIGN_CONNECTION`, or an idempotent script from `dotnet ef migrations script --idempotent`. `/health/ready` stays `Unhealthy` while a migration of the build is missing.
- **Rollback.** `InitialInventorySchema`'s `Down` refuses (error 50100) while any item, reservation or unpublished event exists. Stock and its history are never discarded by a migration. `InventoryListingOrder` rolls back to the previous index without data loss. A production rollback of the code is done by deploying the previous build: the schema is backward compatible.

## 6. Failures and recovery

| Situation | Effect | What answers | Recovery |
|---|---|---|---|
| SQL Server unavailable, a lock wait over 5 s, or deadlock retries exhausted | Nothing is written; readiness fails while unreachable | `503` with `Retry-After: 5` | Automatic once it is back. Clients retry with the same `Idempotency-Key`. |
| Tenant & Merchant unavailable | Merchant requests refused before any read or write | `503 membership-unavailable` | Automatic. The reservation API does not depend on it. |
| Catalog unavailable | Item creation refused | `503 catalog-unavailable` | Automatic. Everything else does not depend on Catalog. |
| Kafka unavailable | Business requests unaffected; rows accumulate; `/health/outbox` `Degraded` | – | The relay backs off and drains in order once Kafka is back ([TENANT_MERCHANT_EVENT_RELAY.md section 6](TENANT_MERCHANT_EVENT_RELAY.md)) |
| A poison row (missing topic, authorization, rejected message) | The relay halts at the row and never skips it; logged (3312) | – | The halted-relay runbook of [TENANT_MERCHANT_EVENT_RELAY.md section 6](TENANT_MERCHANT_EVENT_RELAY.md), with the Inventory database and topic |
| Outbox relay stopped (no leader) | As for Kafka unavailable | – | Any instance takes the leadership on its next attempt |
| Expiry worker stopped on every instance | Overdue reservations keep their stock reserved; availability is under-reported, never over-reported; `/health/expiry` `Degraded` | Commit and release still expire overdue reservations (`409 reservation-expired`, or `200 Expired` on release) | Start the worker. It expires every overdue reservation it finds. |
| An instance crashes in the middle of an expiry or a write | The transaction rolls back | – | Nothing to do: the reservation is still active and overdue, and a worker picks it up |
| A deadlock between two writers | One is retried whole, up to 3 times | `503` only if retries are exhausted | Automatic. The fixed lock order makes it rare. |
| A concurrency conflict (`rowversion`) | Should not happen: the row locks serialize writers | `409 concurrency-conflict` | Retry |
| An invalid or unauthorized service token at the reservation API | Refused | `401` or `403` | Fix the caller's client registration or the allow-list |
| Inventory's own token refused by Identity | Merchant requests and item creation fail closed | `503` | Fix the `svc-inventory` registration or key |

**The database is authoritative.** Events are derived from it, through the outbox, at least once. A consumer that missed events can rely on the item and reservation versions, and on the eventual redelivery.

## 7. Verification level and production gates

- **Tested locally** against SQL Server 2022 (Compose) and Kafka 4.4.0 (KRaft, Compose). Not CI-verified until pushed. Evidence: [PHASE_5_INVENTORY.md](../architecture/PHASE_5_INVENTORY.md).
- **Production gates (open):**
  - the `svc-inventory` key in a managed key store
  - the deployed Identity client registration, and the allow-list entries at Tenant & Merchant and Catalog
  - mutual TLS to Kafka, production topic settings (replication 3, `min.insync.replicas=2`, partitions from measured load)
  - deployed Gateway routes and TLS
  - a metrics exporter and the alerts above
  - a retention decision for the movement ledger
  - load measurement on production-like hardware
  - a green CI run
