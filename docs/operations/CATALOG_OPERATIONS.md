# Catalog Operations

How to run, configure and troubleshoot the Catalog service (Phase 4).

- **Design:** [PHASE_4_CATALOG.md](../architecture/PHASE_4_CATALOG.md).
- **Decisions:** [ADR-024](../decisions/ADR-024-catalog-service-design.md) and [ADR-025](../decisions/ADR-025-merchant-authorization-through-tenant-merchant-membership-verification.md).
- **Contract:** [CATALOG_API_CONTRACT.md](../api/CATALOG_API_CONTRACT.md).

## 1. Local start

1. **Start MongoDB.** A locally installed `mongod` usually holds port 27017 (CLAUDE.md), hence 27018:

   ```bash
   cd deploy/docker && MONGO_PORT=27018 docker compose --profile mongodb up -d --wait
   ```

2. **Create the development keys.** This is needed once, for merchant requests. The script writes the `svc-catalog` client key outside the repository, and the settings scripts for Identity, Tenant & Merchant and Catalog:

   ```bash
   dotnet run deploy/dev/bff-dev-keys.cs
   ```

   Load `identity-clients.*` before starting Identity, `tenant-merchant-identity.*` before Tenant & Merchant (it includes `TenantMerchant__MembershipVerification__AllowedClientIds__0=svc-catalog`), and `catalog-tenant-merchant.*` before Catalog.

3. **Run Catalog** (`https://localhost:7103`, `http://localhost:5103`):

   ```bash
   export ConnectionStrings__CatalogDatabase='mongodb://localdev:<MONGO_ROOT_PASSWORD>@127.0.0.1:27018/?authSource=admin'
   dotnet run --project src/Services/Catalog/EnterpriseCommerce.Catalog.Api --launch-profile https
   ```

4. **Check it:**
   - `/health/live` answers `Healthy` at once.
   - `/health/ready` answers `Healthy` once the indexes exist, normally within a second.
   - Through the Gateway (`dotnet run --project src/Gateway/EnterpriseCommerce.Gateway --launch-profile http`, then `https://localhost:7000`), `GET /api/v1/catalog/products` reaches Catalog. `/internal/v1/catalog/**` answers `404` from the Gateway.

**Without step 2,** Development starts anyway: public discovery works, and every merchant request answers `503 membership-unavailable`.

**Without a taxonomy client,** no category can be created. The allow-list is empty by default, and Admin does not exist yet. Locally, the integration tests create categories with a test client. To experiment by hand, register a `svc-` service client at Identity with the scope `svc.catalog.taxonomy` and the audience `catalog-api` ([CATALOG_API_CONTRACT.md section 6](../api/CATALOG_API_CONTRACT.md#6-internal-category-taxonomy)), and allow-list it in `Catalog__Taxonomy__AllowedClientIds__0`. Never commit such a client.

## 2. Configuration

| Setting | Default | Notes |
|---|---|---|
| `ConnectionStrings:CatalogDatabase` | none, required | A MongoDB connection string. It comes from an environment variable or user secrets and is never committed. Locally it uses the Compose root user; a least-privilege `catalog` user is a production gate. |
| `Catalog:Database:DatabaseName` | `catalog` | One database per service (ADR-003) |
| `Catalog:Database:ServerSelectionTimeout` | `00:00:05` | How long a request waits for MongoDB before `503` (at most 60 s) |
| `Catalog:Database:QueryTimeout` | `00:00:05` | The server time limit (`maxTimeMS`) of every query (at most 60 s) |
| `Catalog:Authentication:Issuer` | `https://localhost:7101/` in Development | Identity's public issuer. HTTPS is required outside Development. |
| `Catalog:Authentication:MetadataAddress` | the issuer's metadata | An internal metadata address |
| `Catalog:Authentication:ServiceAudience` | `catalog-api` | |
| `Catalog:Authentication:UserAudiences` | `["ec-merchant"]` | Front ends whose users may call the merchant API |
| `Catalog:Taxonomy:AllowedClientIds` | empty | `svc-` clients that may write categories |
| `Catalog:MembershipVerification:BaseAddress` | none | Tenant & Merchant's internal address |
| `Catalog:MembershipVerification:TokenEndpoint` | none | Identity's token endpoint |
| `Catalog:MembershipVerification:Issuer` | `Catalog:Authentication:Issuer` | The audience of the client assertion |
| `Catalog:MembershipVerification:ClientId` | `svc-catalog` | |
| `Catalog:MembershipVerification:PrivateKeyPath` | none | A PEM file outside the repository |
| `Catalog:MembershipVerification:KeyId` | none | The `kid` registered for the client |
| `Catalog:MembershipVerification:Timeout` | `00:00:05` | 0 to 30 s |

**Start-up validation.** The host refuses to start when:
- the connection string is missing or malformed
- the security settings are invalid
- outside Development, the membership verification is not configured, or not on `https`

A partial membership configuration is always an error.

**Tenant & Merchant side.** `TenantMerchant__MembershipVerification__AllowedClientIds__0=svc-catalog` is required, and Identity must have the `svc-catalog` client with the scope `svc.tenant-merchant.membership-verification` and the audience `tenant-merchant-api`.

## 3. Indexes and readiness

**Index setup.** At start, a background initializer creates the 15 named indexes ([PHASE_4_CATALOG.md section 5](../architecture/PHASE_4_CATALOG.md#5-indexes)). Creation is idempotent, so a restart is safe.

**While the indexes are missing:**
- `/health/ready` is `Unhealthy`
- every write answers `503`, so uniqueness is never unprotected

Reads work as soon as MongoDB answers.

**Log events:**

| Event | Meaning | Action |
|---|---|---|
| 4000 (Information) | Indexes ensured; writes accepted | none |
| 4002 (Warning) | An attempt failed because MongoDB was unreachable or timed out. Retries back off from 1 s up to 30 s. | Restore MongoDB connectivity |
| 4001 (Error) | **Permanent** failure: an index option or key conflict (codes 85 and 86), or duplicate stored values while building a unique index (11000). The service stays not ready and refuses writes until it restarts. | Inspect the indexes (`db.products.getIndexes()`). Never drop a unique index without understanding the conflict. Remove or rename the conflicting index, or correct the duplicate documents, then restart. |

## 4. Health, metrics and logs

**Health:**
- **Liveness** (`/health`, `/health/live`) never depends on MongoDB.
- **Readiness** (`/health/ready`) requires a MongoDB ping and the indexes.

Neither depends on Tenant & Merchant. A Tenant & Merchant outage affects merchant requests only (`503`), never public discovery or readiness.

**Meters.** The meter `EnterpriseCommerce.Catalog` has no exporter yet (observability phase). Its tags are bounded:

| Instrument | Tags |
|---|---|
| `catalog.membership_checks` | `outcome`: `allowed`, `not_visible`, `forbidden` or `unavailable` |
| `catalog.store_unavailable` | `operation`: `read` or `write` |

**Logs:**

| Event IDs | Area | Notes |
|---|---|---|
| 1000-1002 | The exception handler | |
| 4000-4002 | Index setup | |
| 4100-4102 | Access denials | Security events. They carry the subject and the tenant ID, never a role value. |
| 4200-4204 | The Tenant & Merchant client | Never the subject, the request body or a token |

Request logs carry the path only, never the query string. Search terms therefore stay out of logs.

## 5. Troubleshooting

| Symptom | Cause and action |
|---|---|
| Every merchant request answers `503 membership-unavailable` | Look for log event 4200 (not configured), 4201 (no service token: check the `svc-catalog` client at Identity and the key path), 4202 (Tenant & Merchant unreachable) or 4203 (a non-200 answer; `403` means `svc-catalog` is not in Tenant & Merchant's verification allow-list). |
| A merchant gets `404 tenant-not-found` for their own tenant | Tenant & Merchant says the membership is not active, or the tenant is not active. Check the membership there. Catalog never decides this itself. |
| Writes answer `503`, reads work | The indexes are not ready (section 3). |
| `401` for a merchant-portal user | The token's audience is not `ec-merchant`, or the issuer or metadata address does not match Identity. |
| Categories cannot be created | The taxonomy allow-list is empty (section 1). |

## 6. Production gates (not done)

- **Database user.** A dedicated least-privilege MongoDB user for the `catalog` database, with TLS to MongoDB.
- **Replica set.** A replica set for availability. It is also the prerequisite for retryable writes and any future outbox (ADR-024 section 13).
- **Backups.** Backup and restore procedures.
- **Keys.** The `svc-catalog` private key in a managed key store.
- **Deployed routing.** Gateway routes and TLS host names per environment.
- **Load.** Index sizing and query performance measured under realistic data. No performance claim is made.
