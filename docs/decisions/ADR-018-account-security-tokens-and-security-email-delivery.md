# ADR-018: Account Security Tokens and Security Email Delivery

- **Status:** Accepted
- **Date:** 2026-11-18
- **Phase:** 2.2 (authentication flows and security design)
- **Resolves:** ADR-015 open question O9 (email confirmation and password reset token semantics), and decisions D10, D11 and D13 of [PHASE_2_2_AUTHENTICATION_FLOWS.md](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#11-decisions-and-implementation-plan). It supersedes no earlier ADR.
- **Related:** [ADR-017](ADR-017-token-session-and-protocol-strategy.md) (authoritative transaction, security version), [ADR-012](ADR-012-transactional-outbox-and-asynchronous-commands.md) (outbox, deferred), [SERVICE_CATALOG.md](../architecture/SERVICE_CATALOG.md) (Notification owns email delivery).

## Context

The baseline contract requires confirmation and reset tokens that are single-use, time-limited and stored hashed ([IDENTITY_API_CONTRACT.md](../api/IDENTITY_API_CONTRACT.md)). ASP.NET Core Identity's default tokens are not stored, and email confirmation tokens stay replayable until they expire (V32, V33, V46).

PoC-5 showed that a persisted token, consumed by a conditional update in the same transaction as the account change, gives exactly one winner under concurrent replay, and rolls back completely when the password policy fails.

The emails that carry these tokens are credentials in transit. Notification owns email delivery, and sensitive data must never travel in integration events. The transactional outbox (ADR-012) has no approved implementation design yet.

## Decision

### 1. Account security tokens

- **Generation:** 256 bits from a cryptographically secure random generator, encoded URL-safe.
- **Storage:** only the **SHA-256 hash**, with the user, purpose (`email-confirmation` or `password-reset`), creation time and expiry, and consumption and supersession times. The raw token exists only in memory until it is handed to email delivery.
- **Single use, expiry and supersession:** issuing a new token supersedes the user's older unconsumed tokens of the same purpose. A password reset also supersedes the user's other outstanding tokens.
- **Atomic conditional consumption:** one conditional update that succeeds only if the hash and purpose match and the token is not consumed, not superseded and not expired. It runs **inside the authoritative transaction** of [ADR-017](ADR-017-token-session-and-protocol-strategy.md) section 8, together with the account change and the security-version update. Any failure rolls everything back, so the token stays usable until it expires.
- Lookup is by hash. Links carry only the token, never the user ID or email address. Comparisons of secrets use constant-time operations wherever a value is compared in memory.
- **GET never consumes a token.** Confirmation and reset links open an Identity page that submits the token by **POST**. These pages send `Referrer-Policy: no-referrer` and `Cache-Control: no-store`. Their query strings are redacted in logs and traces.
- **Initial lifetimes (configuration):** 24 hours for confirmation, 30 minutes for reset.

### 2. Account rules

- A **confirmed email is required** before sign-in.
- A **dedicated account status** represents disablement. Lockout is never misused for it.
- **Progressive per-account delay and per-IP throttling** protect login, registration, resend and reset.
- **Resend limits:** at most 3 security emails per address per purpose per hour (initial configured value). Excess requests are silently dropped.
- **Neutral responses:** the user sees the same response whether or not the account exists, is confirmed, or was rate limited. A visible response never depends on delivery success.

### 3. Security email delivery

- **Notification owns production email delivery.** Identity owns token creation, validation and consumption.
- **MVP delivery path:** Identity places a delivery request in a **bounded in-process queue**. A worker in Identity hands the email to Notification through an **authenticated synchronous internal call** with a service token (E2 in the Phase 2.2 document). The raw link never travels in Kafka.
- Identity persists a **delivery status record** without the link or token: delivery ID, user, purpose, status and timestamps. Statuses: `Queued`, `Dispatching`, `Sent`, `Failed`, `Dropped`, `Indeterminate`.
- If the queue is full, the request is recorded as `Dropped` and the user-visible response stays neutral.
- **Status-only reconciliation** (a periodic job):
  - a stale `Queued` record becomes `Dropped`, for example after a process restart lost the in-memory item
  - a stale `Dispatching` record becomes `Indeterminate`, because the handoff may or may not have happened
  - reconciliation **never resends and never mints a token**. A raw token can no longer be recovered, and minting from a background job would bypass rate limits and supersession.
- The worker's claim of a record (`Queued` → `Dispatching`) and reconciliation's state changes are conditional updates, so they cannot both win.
- **Recovery is user-driven:** the user requests a new email, which mints a new token and supersedes the old one, subject to the resend limits.
- **Outbox-based delivery (E3, opaque-reference handoff) is deferred** until the outbox design of ADR-012 is approved.
- **Notification not yet implemented:** Identity refuses to start outside Development unless a production delivery adapter is configured. Until the Notification service exists, Identity can only run in Development.

### 4. Development-only mail sink

A development adapter delivers security emails to a local mail sink, for example a loopback SMTP catcher in a Compose profile. It is isolated by **all five layers**:

1. **Debug-only project reference:** the adapter lives in its own project, referenced by Identity.Api only when the build configuration is `Debug`.
2. **`#if DEBUG` registration:** the registration code does not exist in Release builds.
3. **Runtime guard:** it is enabled only when the environment is `Development` **and** an explicit configuration flag is set **and** the SMTP host is a loopback address or the Compose mail-sink host.
4. **Fail-fast production checks:** outside Development, startup fails if the adapter is registered or its flag is set.
5. **CI checks:** architecture tests restrict the adapter project's references and consumers, and a CI step verifies that the Release publish output contains no adapter assembly.

## Consequences

**Positive:**

- Strict single use under concurrency is guaranteed by the database, independent of framework internals (PoC-5).
- A stolen database row cannot be turned into a usable link.
- Service ownership is preserved: Notification delivers, Identity owns account security.
- No credential travels in Kafka, and no raw token is ever persisted.

**Negative and accepted:**

- A queued email can be lost on a process restart. The user then requests a new one. The status record makes the loss visible (`Dropped` or `Indeterminate`).
- A synchronous dependency from Identity's worker to Notification until E3 exists.
- Identity cannot run outside Development until Notification provides the internal delivery endpoint.
- Custom token storage and consumption code, covered by concurrency and rollback tests.

**Required implementation tests:** the single-use consumption race, rollback on policy failure, no raw token persisted, supersession on resend, neutral responses, worker claim versus reconciliation (the planned PoC-15, never executed as a PoC), and the five mail-sink isolation layers.

## Alternatives considered

- **ASP.NET Core Identity Data Protection tokens (Option B):** not stored, confirmation tokens replayable, and single use only as a side effect of the concurrency stamp. Rejected.
- **Identity sends email directly (E1):** changes service ownership. Rejected.
- **Opaque-reference handoff over Kafka now (E3):** depends on the outbox, which is not designed yet. Deferred.
- **Automatic resend by reconciliation:** would need stored raw tokens or minting outside user control. Rejected.
- **Durable queue in Identity's database with stored links:** would persist credentials. Rejected.
