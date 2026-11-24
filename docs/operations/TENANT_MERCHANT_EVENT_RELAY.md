# Tenant & Merchant: Integration Event Relay Operations

How to configure, observe and recover the transactional outbox and its Kafka relay in Tenant & Merchant.

- **Status:** implemented in Phase 3 S4 (2026-11-23).
  - Tested locally against SQL Server 2022, LocalDB and Kafka 4.4.0 (KRaft, single node), and in CI.
  - Nothing is production-verified. See [section 7](#7-verification-level-and-production-gates).
- **Decisions** (this document does not change them):
  - [ADR-023](../decisions/ADR-023-integration-event-publishing-and-transactional-outbox-implementation.md)
  - the design and implementation decisions of [PHASE_3_S4_EVENT_FLOW.md](../architecture/PHASE_3_S4_EVENT_FLOW.md)
- **Contracts:** [docs/events](../events/README.md) and [TENANT_MERCHANT_EVENTS.md](../events/TENANT_MERCHANT_EVENTS.md).
- **Code:** the shared outbox and relay in `src/BuildingBlocks/EnterpriseCommerce.BuildingBlocks.Messaging/` (since Phase 5, [ADR-028](../decisions/ADR-028-inventory-integration-events-and-shared-outbox-building-block.md) section 4, with unchanged behaviour), and the service's profile and mapper in `src/Services/TenantMerchant/EnterpriseCommerce.TenantMerchant.Infrastructure/Messaging/`.

## 1. What runs

1. **The business transaction.** A tenant creation writes five rows in one SQL Server transaction:
   - the tenant
   - its merchant profile
   - the owner membership
   - the audit record
   - one `TenantCreated` v1 row in `dbo.OutboxMessages`

   The HTTP response returns after the commit. It never waits for Kafka.
2. **The relay.** `OutboxRelay` is a hosted service in every Tenant & Merchant instance.
   - **Leadership.** One instance publishes: the holder of the session application lock `outbox-relay`. Its dedicated connection carries the application name `tenant-merchant-outbox-relay`. The other instances stand by and try the lock every five poll intervals.
   - **Publishing.** The leader reads committed pending rows in `Sequence` order, publishes them one at a time and awaits each broker acknowledgement.
   - **Marking.** After each batch, the leader marks the delivered rows `PublishedAt`.
3. **Delivery is at least once.** A crash, restart or leadership change between an acknowledgement and its marking publishes the row again. The duplicate has the same `eventId` and the same bytes. Consumers deduplicate by `eventId` (ADR-023 section 7).

## 2. Configuration

Configuration sections `TenantMerchant:Outbox` and `TenantMerchant:Kafka`. In environment variables they are `TenantMerchant__Outbox__*` and `TenantMerchant__Kafka__*`. All settings are validated at startup, and invalid values stop the host.

| Setting | Default | Allowed | Meaning |
|---|---|---|---|
| `Outbox:Topic` | `tenant-merchant.events` | `[a-z0-9.-]{1,249}` | Written to each new row |
| `Outbox:PollInterval` | `00:00:01` | 50 ms to 1 min | Idle polling. A full batch starts the next at once. |
| `Outbox:BatchSize` | `100` | 1 to 1000 | Rows per batch |
| `Outbox:MaxBackoff` | `00:01:00` | 1 s to 10 min | Cap of the backoff: 1 s doubled per failed attempt, 50 to 100 % jitter |
| `Outbox:HaltAfterAttempts` | `10` | 1 to 1000 | Consecutive failures of the head row before the relay reports itself halted |
| `Outbox:PublishedRetention` | `7.00:00:00` | 1 h to 90 days | Published rows older than this are deleted |
| `Outbox:CleanupInterval` | `01:00:00` | 1 min to 1 day | How often the leader cleans up |
| `Outbox:StaleAfter` | `00:05:00` | 10 s to 1 day | `/health/outbox` turns `Degraded` beyond this pending age |
| `Kafka:BootstrapServers` | none | `host:port[,…]` | **Required outside Development.** In Development without it, the relay is disabled (log event 3300) and committed events stay pending. |
| `Kafka:SecurityProtocol` | `Plaintext` | `Plaintext`, `Ssl` | **Outside Development only `Ssl`**, with a client certificate and key (mutual TLS) |
| `Kafka:SslCaLocation` | system store | PEM file | The CA that verifies the brokers |
| `Kafka:SslCertificateLocation`, `Kafka:SslKeyLocation` | none | PEM files | **Required outside Development.** The key is an unencrypted PEM mounted as a secret. A named file must exist. |
| `Kafka:MessageTimeout` | `00:00:30` | 1 s to 5 min | Bound of one delivery (`message.timeout.ms`) |

**The producer** (fixed, not configurable):
- `acks=all` and `enable.idempotence=true`
- `linger.ms=0` and `allow.auto.create.topics=false`
- `client.id=tenant-merchant-outbox-relay`

**SASL is not supported yet** (production gate):
- The service holds no credential (`ServiceOwnershipTests`).
- A broker credential needs a secret store design.

**Locally:**

```bash
cd deploy/docker
docker compose --profile kafka up -d --wait
docker compose --profile kafka wait kafka-init     # exit code 0: the topics of kafka/topics.txt exist

# Then run Tenant & Merchant with:
TenantMerchant__Kafka__BootstrapServers=127.0.0.1:9094
```

`up --wait` does not wait for the one-shot `kafka-init` to finish, so `wait kafka-init` confirms it. The manifest is [deploy/docker/kafka/topics.txt](../../deploy/docker/kafka/topics.txt). **No application creates a topic.**

## 3. Health

| Endpoint | Checks | Part of readiness? |
|---|---|---|
| `/health/live`, `/health` | Nothing (the process) | No |
| `/health/ready` | The database | It is readiness |
| `/health/outbox` | The relay, below | **No.** A Kafka outage never takes the API out of rotation. Business requests do not need Kafka. |

**`/health/outbox`:**
- It is anonymous, answers the status word only, and has a 5-second timeout.
- `Degraded` answers `200`.
- It is `Degraded` when the oldest pending row is older than `StaleAfter`. This holds on every instance, leader or standby, because the backlog is read from the database.
- It is also `Degraded` when this instance leads and is backing off, is halted, or cannot see its topic.
- It is `Degraded` when the database cannot be read.

## 4. Metrics

Meter `EnterpriseCommerce.TenantMerchant.Outbox`. No exporter is wired until the observability phase.

| Instrument | Type | Tags |
|---|---|---|
| `outbox.published` | counter | none |
| `outbox.publish_failures` | counter | `category` |
| `outbox.halts` | counter (transitions into the halted state) | `category` |
| `outbox.publish.duration` | histogram, seconds | none |
| `outbox.cycle.duration` | histogram, seconds | none |
| `outbox.pending` | observable gauge | none |
| `outbox.oldest_pending_age_seconds` | observable gauge | none |
| `outbox.leader` | observable gauge, 0 or 1 | none |
| `outbox.halted` | observable gauge, 0 or 1 | none |

- **Tags.** `category` is one of `transient`, `topic`, `authorization`, `rejected`, `configuration` and `unknown`. No tenant, event or correlation ID is ever a tag.
- **Alert candidates:**
  - `outbox.halted = 1`
  - `outbox.oldest_pending_age_seconds > 300`
  - no instance with `outbox.leader = 1` for several minutes
  - a sustained rate of `outbox.publish_failures`

## 5. Logs

Events 3300 to 3332 are structured.

- **Field names.** They carry the event type, event ID, version, tenant ID (the message key), correlation ID, failure category and broker error code.
- **What they never carry:** the envelope, a display name, subject IDs or secrets.

| Event | Level | Meaning |
|---|---|---|
| 3300 | Warning | Relay disabled: no `BootstrapServers` (Development only) |
| 3301, 3302, 3303 | Information, Warning, Information | Leadership acquired, lost, released on shutdown |
| 3304 | Warning | The leadership check failed (database) |
| 3310 | Debug | Batch published |
| 3311 | Warning | A publication failed; retrying after the backoff |
| 3312 | Error | **Halted** at a row: a permanent failure or `HaltAfterAttempts` failures |
| 3313 | Error | Still halted (every 10th attempt) |
| 3314 | Error | Topic missing, not authorized or unreachable; it is never created |
| 3315 | Warning | A relay cycle failed (database or unexpected); backing off |
| 3320 | Information | Cleanup deleted published rows |
| 3321 | Debug | The backlog could not be read |
| 3330, 3331, 3332 | Debug, Error, Warning | Producer connection error, fatal producer state (it is rebuilt), metadata read failure |

## 6. Failures and recovery

| Situation | What happens | What to do |
|---|---|---|
| Kafka down | Business requests succeed and rows accumulate. Each attempt is recorded on the head row (`Attempts`, `LastAttemptAt`, `LastError`). The backoff grows to `MaxBackoff`. After `HaltAfterAttempts` it is reported as halted. | Restore the broker. The relay resumes by itself and drains the backlog in `Sequence` order. |
| Topic missing or not authorized | The relay backs off at the topic check, publishes nothing and never creates the topic (3314, `Degraded`). | Create the topic from the manifest (or IaC), or fix the ACL. The relay resumes by itself. |
| A row the broker refuses (`rejected`, `authorization`, `configuration`) | Halted at once (3312): nothing after the row is published, to keep per-tenant order. It is retried at the capped backoff. | See the runbook below |
| Relay process crash, restart or handover | The next leader resumes at the oldest pending row. The stored attempts keep the backoff. | Nothing. Duplicates are possible and carry the same `eventId`. |
| Database down | The relay backs off (3315). Readiness is `Unhealthy` through the database check. | Restore the database |
| A leader frozen with an open session | It keeps the lock and nothing is published (known limit) | Restart the frozen instance. Its session ends and a standby takes over. |

**Runbook: a halted relay.**

1. Find the head row:

   ```sql
   SELECT TOP (1) Sequence, EventId, EventType, EventVersion, MessageKey, Attempts, LastAttemptAt, LastError
   FROM dbo.OutboxMessages WHERE PublishedAt IS NULL ORDER BY Sequence;
   ```

   `LastError` is `<category>:<broker error code>`, for example `Topic:UnknownTopicOrPart`.
2. Fix the cause: the topic, the ACL, the TLS files, the configuration or the broker. The relay resumes by itself at its next attempt, at most `MaxBackoff` later. A restart is not needed.
3. **Only if the row itself can never be published, and only after a documented review:** republish the fact in a corrected form before marking the row.
   - **Never delete a pending row.** It is a committed fact that consumers have not seen.
   - **Never mark one published by hand without that review.** Downstream projections would silently miss the fact.

**Other procedures:**
- **The backlog after an outage:** `SELECT COUNT(*), MIN(OccurredAt) FROM dbo.OutboxMessages WHERE PublishedAt IS NULL`.
- **Rolling back the migration** `IntegrationEventOutbox` refuses while rows are pending (error 50004). Publish them first.

## 7. Verification level and production gates

**Implemented and tested** (locally and in CI; evidence in [PHASE_3_S4_EVENT_FLOW.md section 10](../architecture/PHASE_3_S4_EVENT_FLOW.md#10-test-evidence)):
- atomic outbox writes on both creation paths
- committed-only reads
- leadership and takeover
- sequential, ordered publication
- redelivery with identical bytes
- the outage and drain
- a missing topic
- health, metrics and configuration validation

**Production gates (open):**

| Gate | Note |
|---|---|
| Kafka authentication | mTLS is supported. SASL needs a secret store design. ACLs: the producer may write only its topic. |
| Topic settings | Replication factor 3, `min.insync.replicas=2`, and the partition count and retention of `tenant-merchant.events` |
| Topics as infrastructure as code | Locally the manifest and `kafka-init` |
| Alerting | On `/health/outbox` and the meters of section 4 |
| Load and throughput | Sequential publication by one leader is not measured |
| A frozen leader | A process that hangs with an open SQL session keeps the lock. Liveness probes and restarts are the mitigation. |
| Exporter | OpenTelemetry and trace propagation (observability phase) |
