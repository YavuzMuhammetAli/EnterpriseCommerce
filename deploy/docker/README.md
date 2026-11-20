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
| `kafka`     | Kafka (KRaft, single node) | `apache/kafka:4.4.0`                    | `localhost:9094`  | 1 GB (heap 512 MB) |
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
| Kafka      | bootstrap servers `localhost:9094` (containers on the compose network use `kafka:9092`) |

Services will receive connection settings through configuration and environment variables when they adopt these components. No connection string is hard-coded in source.

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
- `auto.create.topics.enable=false`: topics are created explicitly once event contracts exist (see [docs/events](../../docs/events/README.md)).
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
