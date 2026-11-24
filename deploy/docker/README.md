# Local Infrastructure (Docker Compose)

Local development infrastructure for EnterpriseCommerce. Application containers are **not** part of this file yet. The Identity API and the Gateway run on the host with `dotnet run` (see the root [README](../../README.md)).

## First-time setup

```bash
cd deploy/docker
cp .env.example .env    # placeholder, local-only credentials; .env is gitignored
```

Every service needs the variables in `.env`. `docker compose` stops with a clear error message if `.env` is missing.

## Profiles

No service starts by default. Choose what you need:

| Profile     | Service    | Image                                                   | Host endpoint     | Memory cap |
|-------------|------------|---------------------------------------------------------|-------------------|------------|
| `sqlserver` | SQL Server | `mcr.microsoft.com/mssql/server:2022-CU28-ubuntu-22.04` | `localhost:1433`  | 2.5 GB (engine limited to 2 GB) |
| `mongodb`   | MongoDB    | `mongo:8.0.38`                                          | `localhost:27017` | 1 GB (WiredTiger cache 0.5 GB) |
| `redis`     | Redis      | `redis:8.8.4-alpine`                                    | `localhost:6379`  | 384 MB (maxmemory 256 MB) |
| `kafka`     | Kafka (KRaft, single node) | `apache/kafka:4.4.0`                    | `127.0.0.1:9094`  | 1 GB (heap 512 MB) |
| `mailsink`  | Mailpit (development mail catcher) | `axllent/mailpit:v1.34.0` | SMTP `localhost:1025`, inbox `http://localhost:8025` | 128 MB |
| `all`       | SQL Server, MongoDB, Redis and Kafka | | | about 5 GB in total |

```bash
docker compose --profile sqlserver up -d --wait     # start one component and wait for healthy
docker compose --profile kafka --profile redis up -d --wait
docker compose --profile all up -d --wait           # everything (about 5 GB RAM)
docker compose --profile all ps                     # status and health
docker compose --profile all down                   # stop, keep data
docker compose --profile all down -v                # stop and DELETE all local data volumes
```

> Always pass the same `--profile` flags to `down` that you used with `up`. Compose only acts on services from active profiles.

## Conventions

- **Pinned images.** No `latest` tags. Image upgrades are deliberate changes in a pull request.
- **Ports bind to `127.0.0.1` only.** Nothing is exposed on your network.
- **Named volumes** (`enterprisecommerce_sqlserver-data`, `enterprisecommerce_mongodb-data`, `enterprisecommerce_mongodb-config`, `enterprisecommerce_redis-data`, `enterprisecommerce_kafka-data`) keep data across restarts.
- **Health checks** on every service, so `up --wait` returns only when the services are usable.
- **Credentials** come only from `.env`. The values in `.env.example` are public placeholders for local development and must never be used elsewhere.

## Port conflicts

If a host port is already in use (for example, by a locally installed MongoDB or SQL Server), change the port in `.env` (`MSSQL_PORT`, `MONGO_PORT`, `REDIS_PORT`, `KAFKA_EXTERNAL_PORT`) or override it for a single run:

```bash
MONGO_PORT=27018 docker compose --profile mongodb up -d --wait
```

Container-to-container ports don't change. Only the host-side binding moves.

## Memory

The memory caps add up to about 5 GB with `--profile all`. Docker Desktop on WSL2 often gives its VM less than that by default (check with `docker info --format '{{.MemTotal}}'`). Either start only the profiles you need, or raise the WSL2 limit in `%UserProfile%\.wslconfig`:

```ini
[wsl2]
memory=8GB
```

## Connecting from the host

| Component  | Example connection setting (local only) |
|------------|------------------------------------------|
| SQL Server | `Server=localhost,1433;User Id=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True` |
| MongoDB    | `mongodb://<MONGO_ROOT_USERNAME>:<MONGO_ROOT_PASSWORD>@localhost:27017/?authSource=admin` |
| Redis      | `localhost:6379,password=<REDIS_PASSWORD>` |
| BFF session Redis (`bff` profile) | `localhost:6380,password=<REDIS_SESSIONS_PASSWORD>` |
| Kafka      | bootstrap servers `127.0.0.1:9094` (containers on the compose network use `kafka:9092`) |

Services will receive connection settings through configuration and environment variables when they adopt these components. No connection string is hard-coded in source.

One SQL Server instance hosts every SQL service's own database (ADR-011 rule 5), created by that service's migrations:

| Service | Setting (environment or user secrets, never committed) | Example database |
|---|---|---|
| Identity | `ConnectionStrings__IdentityDatabase` | `EnterpriseCommerce_Identity` |
| Tenant & Merchant | `ConnectionStrings__TenantMerchantDatabase` | `EnterpriseCommerce_TenantMerchant` |
| Inventory | `ConnectionStrings__InventoryDatabase` | `EnterpriseCommerce_Inventory` |

Catalog is the MongoDB service (ADR-011). Its database is `catalog` by default (`Catalog__Database__DatabaseName`), and its connection string uses the MongoDB setting above (with `MONGO_PORT=27018` when a local `mongod` holds 27017):

| Service | Setting (environment or user secrets, never committed) | Example |
|---|---|---|
| Catalog | `ConnectionStrings__CatalogDatabase` | `mongodb://localdev:<MONGO_ROOT_PASSWORD>@127.0.0.1:27018/?authSource=admin` |

The Compose MongoDB is a standalone node: it has no multi-document transactions, which is why Catalog writes single documents only and publishes no events yet ([ADR-024](../../docs/decisions/ADR-024-catalog-service-design.md) section 13). Catalog creates its indexes itself at startup. Operations: [CATALOG_OPERATIONS.md](../../docs/operations/CATALOG_OPERATIONS.md).

Append `;Database=<name>` to the SQL Server setting above. In Development, `Identity__Database__ApplyMigrationsAtStartup=true` `TenantMerchant__Database__ApplyMigrationsAtStartup=true` and `Inventory__Database__ApplyMigrationsAtStartup=true` apply the migrations at startup. Integration tests create and drop their own throwaway databases (`EC_TEST_SQLSERVER`, [docs/testing](../../docs/testing/README.md)).

## BFF session Redis (`bff` profile)

The `redis-sessions` service is the dedicated session store of the backend-for-frontend (ADR-016 section 3). It is never used for caching.

- `maxmemory-policy noeviction`: a full store fails writes, and the BFF then fails closed, instead of silently evicting active sessions. The BFF refuses to start against any other policy, and its `/health` reports it.
- Append-only persistence (`appendfsync everysec`), so sessions survive a restart.
- It has its own password (`REDIS_SESSIONS_PASSWORD`) and port (`REDIS_SESSIONS_PORT`, default 6380).
- The general `redis` service keeps `allkeys-lru`.

```bash
docker compose --profile bff up -d --wait
docker compose exec redis-sessions redis-cli config get maxmemory-policy   # noeviction
```

## Kafka notes

- KRaft mode. One node acts as both broker and controller. There is no ZooKeeper.
- `auto.create.topics.enable=false`: topics are created explicitly, never by an application ([docs/events](../../docs/events/README.md)).
- The topics are declared in [kafka/topics.txt](kafka/topics.txt). The one-shot `kafka-init` service creates them once the broker is healthy (idempotent, `--if-not-exists`). `up -d --wait` does not wait for a one-shot container to finish, so confirm with `docker compose --profile kafka wait kafka-init` (exit code 0).
- Tenant & Merchant publishes to `tenant-merchant.events` with `TenantMerchant__Kafka__BootstrapServers=127.0.0.1:9094` ([TENANT_MERCHANT_EVENT_RELAY.md](../../docs/operations/TENANT_MERCHANT_EVENT_RELAY.md)).
- Inventory publishes to `inventory.events` with `Inventory__Kafka__BootstrapServers=127.0.0.1:9094` ([INVENTORY_OPERATIONS.md](../../docs/operations/INVENTORY_OPERATIONS.md)). On a broker started before Phase 5, run `docker compose --profile kafka run --rm kafka-init` once to create the new topic.
- The external listener is published on `127.0.0.1` only, and advertises `127.0.0.1:9094`, not `localhost`: where `localhost` resolves to `::1` first (Windows), a client of an advertised `localhost` tried IPv6 first and waited about 2 seconds for each refused connect ([docs/testing TS-5](../../docs/testing/README.md)). Use `127.0.0.1:9094` as the bootstrap servers too. A broker created before this change still advertises `localhost`; `docker compose --profile kafka up -d --wait` recreates it with the new setting and keeps its data volume.
- Replication factor is 1, which is suitable for local development only.

## Not included yet

The observability stack (OpenTelemetry Collector, Prometheus, Grafana, Loki) is planned for a later phase and will be added as separate profiles (for example `observability`).

## Development mail sink (Identity security emails)

Identity sends confirmation emails through the Notification integration. Notification is not implemented yet, so in
Development you can route them to a local mail catcher instead (ADR-018 section 4):

```bash
docker compose --profile mailsink up -d --wait
# Debug build of Identity.Api, Development environment, explicit opt-in (never committed):
Identity__DevelopmentMailSink__Enabled=true dotnet run --project src/Services/Identity/EnterpriseCommerce.Identity.Api --launch-profile https
# Open http://localhost:8025 to read the emails.
docker compose --profile mailsink down
```

The sink is accepted only on loopback or the Compose host `mailpit`. Release builds do not contain it, and outside
Development Identity refuses to start if its settings or assembly are present.
