# Identity Operations and Hardening

- **Status:** implemented in Phase 2.3 slice S6.
- **Scope:**
  - account disablement and reactivation
  - retention cleanup
  - signing and encryption key rollover
  - Data Protection keys
  - health
  - metrics
  - the remaining production deployment gates
- **Decision sources** (this document does not change them):
  - [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md)
  - [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)
  - [PHASE_2_2_AUTHENTICATION_FLOWS.md](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md), especially section 6.5 and the implementation findings in section 11.7 (I31 to I37)
- **API contract:** [IDENTITY_API_CONTRACT.md section 2.12](../api/IDENTITY_API_CONTRACT.md#212-account-disablement-and-reactivation-internal).

## 1. Account disablement and reactivation

### 1.1 Administrative boundary

- **Admin owns the decision.** Admin owns platform administrators, their roles and the administrative audit trail ([SERVICE_CATALOG.md](../architecture/SERVICE_CATALOG.md)). It authorizes the administrator, then calls Identity.
- **Identity authorizes the calling client, never a role.** The internal endpoints accept only a service token (client credentials) that meets all of these conditions:
  - `ec_ptype=service`
  - an audience equal to `Identity:AccountAdministration:Audience` (default `identity-api`)
  - the scope `svc.identity.account-lifecycle`
  - a `client_id` with the `svc-` prefix that is listed in `Identity:AccountAdministration:AllowedClientIds`

  An empty list denies every caller. User tokens, the Identity SSO cookie and other services' tokens are refused.
- **Edge blocking.** The Gateway answers `404` for `/internal/**` on every host, so internal endpoints are reachable only on the internal network. The service token is still the authorization.
- **Admin does not exist yet.** Until it does, no client is allow-listed outside the tests. Registering Admin's client later needs only configuration:
  - a `Service` client `svc-admin` with `ServiceScopes: [{ Scope: svc.identity.account-lifecycle, Audience: identity-api }]`
  - `svc-admin` added to `AllowedClientIds`

### 1.2 Semantics

| | Disable | Reactivate |
|---|---|---|
| Authoritative transaction (ADR-017 section 8) | Status `Active → Disabled` (conditional), new security stamp and concurrency stamp, reason, actor reference and database time; security version + 1; every outstanding confirmation and reset token superseded | Status `Disabled → Active` (conditional), reason, actor reference and database time. The security version is **not** changed. |
| After commit | Every authorization and token of the user revoked as idempotent cleanup. It also runs when the account was already disabled, so repeating the call completes a failed cleanup. | Nothing. No authorization, refresh token or cookie from before the disablement becomes valid again: the user signs in again. |
| Effect | Login fails with the generic message even with the correct password. Refresh, code exchange and the SSO cookie fail at once (security version). BFF sessions end at their next refresh. Issued access tokens stay valid until they expire (ADR-017 section 6). | The next login works and starts new sessions at the raised version. |
| Concurrency | Two disablements: exactly one changes the account and raises the version once; the other reports `changed: false`. Against a refresh or code exchange: whichever commits first wins the lock on the security version; tokens issued just before cannot be refreshed. Against a password reset: either order ends disabled; a reset ordered after the disablement finds its token superseded. | Against a login: the login sees the committed status only (generic failure before the commit, success after it). |
| Failure | A deadlock retries the whole transaction. Exhausted retries or a missing security-version record give `503` with nothing changed. | Same |

**Audit.** Each operation writes a security event with the subject, the calling client, Admin's actor reference, the reason code, whether the account changed, the security version and the cleanup outcome:

- `AccountDisabled` (event 3500)
- `AccountReactivated` (event 3502)

The account keeps the last change as `StatusChangedAt`, `StatusReason` and `StatusChangedBy`. Admin keeps the full administrative audit trail. No email address or other personal data beyond `sub` is logged.

**Deferred:**

- Forced password reset when a security-related disablement is reactivated (proposal in PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.6.6). Until it exists, that is a procedure: the user resets the password through "forgot password".
- The user-disabled integration event (ADR-015 O13).

## 2. Retention cleanup

### 2.1 Policy per record category

The **replay protection window** is the longest configured token or session lifetime plus a safety margin:

- the lifetimes: every client's access, refresh and absolute session lifetime, plus the code and ID token lifetimes
- the margin: `Identity:Retention:SafetyMargin`, default 1 day, at least 1 hour
- `Identity:Retention:MinimumReplayProtectionWindow` can only lengthen the window

No token Identity issues outlives the longest lifetime, so a token created before `now − window` has expired everywhere. With the development lifetimes (storefront absolute session 30 days), the window is 31 days.

| Category | Cleaned | Rule | Why it is safe |
|---|---|---|---|
| Refresh-token redemption ledger (`RefreshTokenRedemptions`) | **Yes** | `RedeemedAt` older than the database time minus the window | A ledger row rejects a second redemption of the same token. Its token expired at the latest one window after issuance, so no request can present it any more. |
| OpenIddict tokens | **Yes**, through `IOpenIddictTokenManager.PruneAsync` (D18) | Created before the database time minus the window, and no longer acceptable: redeemed, revoked, of a non-valid authorization, or expired | A redeemed entry is what triggers the reuse policy; it is removed only after its token expired. |
| OpenIddict authorizations | **Yes**, through `IOpenIddictAuthorizationManager.PruneAsync`, after the tokens | Created before the threshold, not valid or ad-hoc, and without any token left | A pruned authorization only makes a token more rejected (fail closed). |
| Account security tokens (`AccountSecurityTokens`) | **No. Retention period open** (finding I35) | Safe floor: `ExpiresAt` (an expired token can never be consumed). Referenced by delivery records. | ADR-018 keeps the rows for per-token audit, and no accepted decision sets their retention period. |
| Security email delivery records (`SecurityEmailDeliveries`) | **No. Retention period open** (I35) | Safe floor: at least 24 hours after `CreatedAt` (the daily resend limit reads them), and only terminal statuses | No accepted decision sets their retention period. |
| Accounts and security-version records | Never automatically | Personal-data deletion is a separate design. | |
| Data Protection keys | Managed by ASP.NET Core | Keys expire after 90 days and are never deleted automatically. | |

### 2.2 Cleanup worker

`IdentityRetentionWorker`, section `Identity:Retention`:

| Setting | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | |
| `InitialDelay` | 5 min | First run after startup, so startup is never slowed |
| `Interval` | 1 h | Runs of one instance are sequential and never overlap |
| `BatchSize` | 1000 (1 to 4000) | Rows per ledger delete statement, below SQL Server's lock escalation threshold |
| `MaxBatchesPerRun` | 100 | Ledger batches per run. The rest waits for the next run. |
| `LockTimeout` | 5 s | The longest a ledger delete waits for a row lock (finding I36) |
| `MaxAttempts` | 3 | Attempts per statement for deadlocks, lock timeouts and command timeouts |
| `SafetyMargin`, `MinimumReplayProtectionWindow` | 1 day, none | See 2.1 |

**One instance per run.** One instance runs each cycle through a session-owned SQL Server application lock (`sp_getapplock`, resource `EnterpriseCommerce.Identity.Retention`) on an unpooled connection. Every other instance skips (`outcome=skipped`).

**Statements and batching:**

- Every statement is idempotent and its own short transaction.
- OpenIddict 7.9 prunes in autocommitted batches of 1000 rows (finding I32).
- All expiry decisions use the database clock.

**Failure handling.** A failed category ends only that category of the run. The next run retries it. Logs carry counts and outcomes only (events 2200 to 2212).

## 3. Signing and encryption keys

### 3.1 Configuration

The key ring is configured under `Identity:Keys` (finding I34). The `Signing` and `Encryption` keys are separate certificates (ADR-017 section 12). Each entry has these settings:

| Setting | Meaning |
|---|---|
| `CertificatePath` | A PKCS#12 file. Deployed environments mount it as a secret. It is never committed (`.gitignore` excludes `*.pfx`, `*.pem`, `*.key`). |
| `CertificatePassword` | From a protected store or the environment. Required outside Development. |
| `Status` | `Pending`: published in the JWKS and accepted, never signs. `Active`: exactly one per use. `Retired`: published and accepted until its window has passed. |
| `RetiredAt` | Required for `Retired`. It starts the retention window. |

**Ring-wide settings:**

- `Provider`: only `CertificateFiles` exists. Any other value stops the host: KMS or HSM support is a deployment gate (D19), not claimed here.
- `ValidatorCacheAllowance`: default 24 hours, at most 30 days. It is the longest time a validator may use a cached JWKS. It must be at least:
  - every BFF's `Bff:Metadata:RefreshInterval` (default 1 hour)
  - every service's JWT bearer `AutomaticRefreshInterval` (default 12 hours)

**Key IDs and signing order:**

- Key IDs (`kid`) are the certificates' SHA-256 thumbprints.
- Identity forces the Active keys ahead of OpenIddict's date-based ordering (V24, V44), and a test asserts the issued `kid`.

**Startup fails closed on:**

- a missing, unreadable or corrupt file, or a wrong password
- an RSA key under 3072 bits
- an Active certificate outside its validity period
- an expired Pending certificate
- zero or several Active keys of one use
- a duplicate certificate
- a `Retired` entry without `RetiredAt`, or with one in the future
- outside Development: a passwordless file, or one readable or writable by others on Unix

Error messages name the file only, never its content or password.

**Retirement windows:**

- **Signing key:** the replay protection window + 30 s clock skew + `ValidatorCacheAllowance`. Refresh tokens and authorization codes are signed with the server signing key too (finding I31), so the window must cover them, not only access tokens.
- **Encryption key:** the replay protection window + 30 s.
- **After the window:** the key is not loaded at all. Event 2302 is logged and `identity.keys.retired_dropped` is incremented. Remove the entry and delete the file.

In Development without `Identity:Keys`, Identity uses ephemeral in-memory keys (event 2300). All tokens become invalid on restart.

### 3.2 Scheduled rollover

1. **Stage.**
   - Add the new signing certificate as `Pending` on every instance and restart them (rolling).
   - The key is published at once.
   - Wait at least `ValidatorCacheAllowance` (24 hours by default), so every BFF and service has it cached.
2. **Activate.**
   - Set the new signing key `Active`, and the old one `Retired` with `RetiredAt` set to the current UTC time.
   - Rotate the encryption key at the same time: add a new `Active` one and retire the old one.
   - Restart (rolling). Both generations of instances publish and accept both signing keys, so tokens from either validate everywhere.
   - Refresh tokens issued before the rollover still redeem: tested with the old signing and encryption keys retired.
3. **Remove.**
   - After the retirement window, Identity stops loading the old keys at the next restart.
   - Delete the entries and the files.
   - Removing them earlier does not weaken security. Sessions whose refresh token was signed or encrypted with the removed key end (`invalid_grant`), and their users sign in again.

**Local rehearsal** with persisted keys, written outside the repository:

```bash
dotnet run deploy/dev/identity-dev-keys.cs -- init        # then load identity-keys.ps1 / identity-keys.sh and restart Identity
dotnet run deploy/dev/identity-dev-keys.cs -- stage
dotnet run deploy/dev/identity-dev-keys.cs -- activate
dotnet run deploy/dev/identity-dev-keys.cs -- prune
```

### 3.3 Emergency rotation (key compromise)

1. Remove the compromised certificate and add a new one as `Active`, with no Pending phase. Restart every instance.
2. Every token signed with the old key fails validation:
   - access tokens
   - refresh tokens, so their sessions end
   - ID tokens
3. Validators react to the unknown `kid`:
   - **BFF:** the handler refreshes the JWKS when it sees an unknown `kid`. The first login per instance can fail once, at most once per `Bff:Metadata:MinimumRefreshInterval` (default 1 minute; finding I37).
   - **Services:** the JWT bearer handler behaves the same way.
4. If the encryption key is compromised as well, rotate it the same way.
5. Consider raising every security version, through logout-all for each affected user, so that SSO cookies end too.

## 4. Data Protection keys

These keys are **not** the token keys. ASP.NET Core Data Protection protects:

- Identity's SSO cookie and antiforgery tokens
- each BFF's OAuth state, nonce cookie and session record contents

| | Identity | BFF (per deployment) |
|---|---|---|
| Section | `Identity:DataProtection` | `Bff` |
| Application name (isolation) | `EnterpriseCommerce.Identity` | `EnterpriseCommerce.Bff.{deployment}` |
| Persistent store | `KeyDirectory`, shared by every Identity instance | `DataProtectionKeyDirectory`, shared by the instances of one deployment |
| Encryption at rest | `KeyEncryptionCertificatePath` / `KeyEncryptionCertificatePassword` | `DataProtectionCertificatePath` / `DataProtectionCertificatePassword` |
| Outside Development | Directory and certificate required; startup fails otherwise. Never a silent ephemeral key ring (finding I33). | Directory, certificate and password required |
| Provider | `FileSystem` only | File system only |

**Deployment requirement:**

- The key directory must be a protected volume shared by the instances: per environment, and for the BFF per deployment.
- The certificate must be a mounted secret.

**Rotation of the key-encryption certificate** (decrypting old keys with a previous certificate) is not implemented yet. Until it is, replacing the certificate makes existing keys unreadable, which signs users out and invalidates antiforgery tokens.

## 5. Health

| Endpoint | Identity | BFF |
|---|---|---|
| `/health/live` | Process only | Process only |
| `/health/ready` | Database (Unhealthy). Active signing and encryption keys: Unhealthy once expired, Degraded within 14 days of expiry. Security email adapter: Degraded in Development without one. Retention and reconciliation workers: Degraded after three missed intervals, never Unhealthy. | Session Redis (`noeviction`) |
| `/health` | Liveness (unchanged) | Session Redis (unchanged S7 behaviour) |

- **Status codes.** Degraded answers `200` and Unhealthy `503`.
- **Bodies and timeouts.** Bodies carry the status word only. Every check times out after 5 seconds.
- **No Redis check in Identity.** Identity does not depend on Redis, so it has no Redis check.
- **Shutdown.** The host's default shutdown timeout (30 seconds) applies. The workers stop at cancellation, and an interrupted retention run leaves nothing half-done. A queued security email can be lost on restart (accepted, ADR-018).

## 6. Metrics

All metrics are on the meter `EnterpriseCommerce.Identity.Security`. No exporter is wired yet (observability phase). Every tag has a fixed value set; identifiers, email addresses and tokens are never labels, and a test enforces it.

| Instrument | Tags |
|---|---|
| `identity.authentication.attempts` | `outcome`: succeeded, failed, locked_out, not_allowed, email_unconfirmed |
| `identity.token.grants` | `grant_type`, `outcome` (issued, denied), `reason` (none, stale_session, account_not_allowed, missing_security_state, reuse, unavailable, presenter_mismatch, session_expired, invalid_grant, invalid_request) |
| `identity.refresh.reuse_detected` | `source` (redemption-ledger, redeemed-token), `action` (revoke_session, revoke_all_sessions, presenter_mismatch) |
| `identity.sso.rejections` | `reason` (absolute_lifetime, stale_session, account_not_allowed) |
| `identity.account.lifecycle` | `action` (disable, reactivate), `outcome` (changed, unchanged, not_found, failed) |
| `identity.security_token.consumption` | `purpose`, `outcome` (consumed, rejected, failed) |
| `identity.security_email.sent`, `.failed`, `.dropped`, `.indeterminate`, `.queue_saturated` | `purpose` or `reason` |
| `identity.security.transaction.deadlock_retries`, `.failures` | `operation`, `reason` |
| `identity.security.cleanup.deadlock_retries`, `.failures` | `operation` |
| `identity.retention.runs`, `.deleted`, `.retries` | `outcome` or `category` |
| `identity.keys.configured` (gauge), `identity.keys.retired_dropped` | `use`, `state` |

**Alert candidates:**

- any `transaction.failures`
- `token.grants{reason=unavailable}`
- `refresh.reuse_detected`
- `retention.runs{outcome=failed}` over several hours
- `keys.retired_dropped`
- a `Degraded` readiness

## 7. Remaining production deployment gates

| Gate | Status |
|---|---|
| KMS or HSM key provider for signing, encryption and Data Protection keys | **Not implemented.** Only mounted PKCS#12 files exist (provider boundary `IKeyMaterialSource`, D19). |
| Shared, protected Data Protection volume per environment and deployment | A deployment task. Not verified in a production-like environment. |
| Key-encryption certificate rotation for Data Protection | Not implemented |
| Admin service and its `svc-admin` registration | Admin does not exist. The internal API is tested with a test client. |
| Notification's internal security email endpoint | Not implemented. Identity refuses to start outside Development without it (ADR-018). |
| Retention periods for account security tokens and delivery records | Open decision (I35) |
| Real Safari, MFA for administrators (D17) | Open (ADR-016 section 8, PHASE_2_2 section 11.3) |
| Metrics exporter and alerting | Observability phase |
