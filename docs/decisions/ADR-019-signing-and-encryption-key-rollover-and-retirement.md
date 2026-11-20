# ADR-019: Signing and Encryption Key Rollover and Retirement

- **Status:** Accepted
- **Date:** 2026-11-20
- **Phase:** 2.3 (Identity implementation, slice S6 reconciliation)
- **Amends:** [ADR-017](ADR-017-token-session-and-protocol-strategy.md) section 12. The sentence "Rotation follows section 6.5 of the Phase 2.2 document" now refers to this ADR. The other decisions of section 12 are unchanged:
  - separate RSA signing and encryption certificates, distinct from TLS and never committed
  - Identity refuses to start outside Development without them
  - the concrete deployed key store stays deferred (D19)
- **Supersedes:**
  - the retirement period of [ADR-015](ADR-015-identity-provider-aspnet-core-identity-and-openiddict.md) R7 ("at least the maximum access token lifetime plus allowed clock skew")
  - steps 2 and 3 of the scheduled rotation in [PHASE_2_2_AUTHENTICATION_FLOWS.md section 6.5](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#65-signing-and-encryption-keys), and the encryption key retention in that section
- **Related:**
  - findings I31, I34 and I37 in [PHASE_2_2_AUTHENTICATION_FLOWS.md section 11.7](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#117-implementation-findings-phase-23)
  - the operational procedure in [IDENTITY_OPERATIONS.md section 3](../security/IDENTITY_OPERATIONS.md#3-signing-and-encryption-keys)
  - [ADR-016](ADR-016-browser-client-architecture-backend-for-frontend.md) (the BFF validates ID tokens)

## Context

ADR-015 R7 and Phase 2.2 section 6.5 sized the retention of a retired signing key by **access tokens**. Section 6.5 step 3 kept the old key for the longest access token lifetime, plus clock skew, plus the JWKS cache duration. Section 6.5 also relied on OpenIddict's certificate-date ordering to switch keys (V24, V44).

Slice S6 found two problems:

- **Refresh tokens and codes are signed too (I31).** OpenIddict signs refresh tokens and authorization codes with the server signing key, then encrypts them. Integration test `KeyRolloverTests.Rollover_KeepsEarlierTokensValid_UntilTheRetiredKeyIsRemoved` shows the effect: a refresh token whose signing key was removed no longer redeems, even while its encryption key is still loaded. With the configured lifetimes, a storefront refresh token signed just before a switch is valid for 7 days, while the old rule kept the key for about 10 minutes plus margins. That rule therefore ends sessions at every rollover. It fails closed, so it is no security weakness, but scheduled rollovers were not meant to sign users out.
- **Date-based selection is implicit (V24, V44).** An expired certificate can still sign when it sorts first. The owner chose explicit key states in configuration instead (I34, 2026-11-20).

**Token lifetimes in effect.** These come from the code and from the only concrete client registrations in the repository: the development registrations in `deploy/dev/bff-dev-keys.cs`, which use the accepted initial values of Phase 2.2 section 6.4. No deployed configuration exists yet.

| Token or bound | Signing key | Encryption key | Lifetime | Source |
|---|---|---|---|---|
| Authorization code | Signed | Encrypted | 60 s | `IdentityServerSetup.AuthorizationCodeLifetime` |
| ID token | Signed | Not encrypted | 5 min | `IdentityServerSetup.IdentityTokenLifetime` |
| User access token | Signed | Not encrypted (ADR-015 R3) | Storefront 10 min, merchant 10 min, admin 5 min | Per-client `AccessTokenLifetime` |
| Service access token | Signed | Not encrypted | 10 min (the code default; no service client sets one). The accepted initial value is 5 min: an open discrepancy, finding I38. | `ClientRegistration.AccessTokenLifetime` |
| Refresh token | Signed (I31) | Encrypted | Each token at most `min(RefreshTokenLifetime, session deadline − issuance)`: storefront 7 days, merchant 2 hours, admin 30 minutes | `ProtocolEndpoints.TokenAsync` (both grants) |
| Absolute session deadline | Not a token | | Storefront 30 days, merchant 12 hours, admin 8 hours | Per-client `AbsoluteSessionLifetime`, private claim set at the code exchange |
| Validator clock skew | | | 30 s | ADR-017 section 4, `IdentityServerSetup.ValidationClockSkew` |

The Identity SSO cookie (1 hour sliding, 12 hours absolute) is protected by Data Protection keys, not by the OpenIddict keys, so it plays no part here.

**Rotation does not extend a retired key's tokens.**

- Each refresh redeems the presented token. With no reuse leeway, a redeemed token is never accepted again (ADR-017 section 5).
- The refresh issues a successor that is signed and encrypted with the **Active** keys.
- The successor's lifetime is capped by the session deadline that was fixed at the code exchange.

A token signed with a retired key therefore has to verify only until its own expiry. A session that started before the rollover continues on successor tokens from the new key, and never beyond its absolute deadline.

## Decision

### 1. Explicit key states

Every signing and encryption key in `Identity:Keys` carries one of three states (I34):

- **`Pending`:** published in the JWKS and accepted for validation. It never signs.
- **`Active`:** exactly one per use. It signs (or encrypts) every new token. Identity places it ahead of OpenIddict's date-based ordering, so the issued `kid` is deterministic.
- **`Retired`:** carries `RetiredAt`. It is still published and accepted until its retention window has passed. It is then no longer loaded.

Key IDs are the certificates' SHA-256 thumbprints. A state change is a configuration change followed by a rolling restart.

### 2. Retention invariant

A retired key stays loaded until every token it signed (signing key) or encrypted (encryption key) has expired, plus the validators' clock skew. The **earliest safe removal time** is:

```text
T_remove ≥ T_last + L_max + skew
```

| Term | Meaning |
|---|---|
| `T_last` | When the last Identity instance stopped using the key as Active, which is the end of the activation rollout |
| `L_max` | The longest lifetime of any token that the key signed or encrypted |
| `skew` | 30 seconds |

**With the configuration above:**

- **Signing key:** `L_max` = 7 days (storefront refresh token). The strict minimum is `T_last + 7 days + 30 s`.
- **Encryption key:** `L_max` = 7 days (storefront refresh token; codes live 60 s). The strict minimum is also `T_last + 7 days + 30 s`.

### 3. Implemented retention rule

Identity keeps a retired key for a fixed period after `RetiredAt`. The period is derived from the **replay protection window** `W` that Identity already computes for retention ([IDENTITY_OPERATIONS.md section 2.1](../security/IDENTITY_OPERATIONS.md#21-policy-per-record-category)).

**`W` is** the longest configured lifetime of any kind, plus `Identity:Retention:SafetyMargin`, raised to `Identity:Retention:MinimumReplayProtectionWindow` if that is longer:

- lifetimes counted: authorization code, ID token, every access token, and every client's refresh and absolute session lifetime
- `SafetyMargin`: default 1 day, at least 1 hour

**Retention periods after `RetiredAt`:**

| Key | Period | With the current configuration |
|---|---|---|
| Signing | `W + 30 s + ValidatorCacheAllowance` | `W` = 30 days + 1 day = 31 days. With the default 24-hour allowance, the key is kept **32 days and 30 seconds**. |
| Encryption | `W + 30 s` | **31 days and 30 seconds** |

**After the period:** Identity no longer loads the key at startup. It logs event 2302 and increments `identity.keys.retired_dropped`. The operator then deletes the configuration entry and the file.

**Why `W` and not the strict `L_max`:**

- `W` already bounds every token kind, so one number governs ledger rows, OpenIddict entries and keys.
- It stays correct even when a client is configured with a refresh lifetime longer than its absolute session.
- It needs no second computed bound.

**The cost:** with the current configuration, a retired key stays published and accepted about 25 days longer than strictly necessary. This is accepted. A compromised key is handled by emergency rotation (section 6), which removes it at once whatever its window.

### 4. Margins

- **Clock skew (30 s).** Every validator accepts a token up to 30 seconds after `exp`: services through the JWT bearer handler (ADR-017 section 4), and Identity's own validation.
- **Activation rollout** (clarified 2026-11-20; the retention rule of section 3 is unchanged). See section 4.1.
- **`ValidatorCacheAllowance`** (default 24 hours, at most 30 days):
  - It is the longest time any validator may use a cached JWKS. It must be at least every BFF's `Bff:Metadata:RefreshInterval` (default 1 hour) and every service's JWT bearer `AutomaticRefreshInterval` (default 12 hours).
  - **Required for staging:** a Pending key must be published for at least this long before it becomes Active.
  - **On removal, not needed for token validity.** It is kept from section 6.5 step 3. A validator that still caches a removed key accepts only tokens that have already expired. The time it adds to the signing key's period is what section 4.1 lets a rollout use.
- **Lowering a lifetime.** Tokens issued under the old lifetime keep their `exp`. While they can still be valid, set `MinimumReplayProtectionWindow` to the previous window, and keep it until that window has passed since the change.

### 4.1 Maximum activation rollout duration

`RetiredAt` is set when the activation rollout **starts**, and startup rejects a `RetiredAt` in the future. Instances that have not yet been restarted keep signing and encrypting with the old keys. `T_last` (section 2) is therefore the **end** of the rollout, not `RetiredAt`.

**The limit.** Section 2 requires `RetiredAt + period ≥ T_last + L_max + 30 s`. The longest permitted rollout, from `RetiredAt` to the moment the last instance stops using the old key, is therefore:

```text
D_max = period − L_max − 30 s
```

| Key | Period (section 3) | `D_max`, general | With the current configuration |
|---|---|---|---|
| Signing | `W + 30 s + ValidatorCacheAllowance` | `W + ValidatorCacheAllowance − L_max` | 31 days + 24 hours − 7 days = **25 days** |
| Encryption | `W + 30 s` | `W − L_max` | 31 days − 7 days = **24 days** |

- **Combined rollout.** Section 5 rotates both keys in the same rollout, so the limit for that rollout is the smaller value: **24 days**. A rollout that changes only the signing key may take up to **25 days**.
- **Host clocks.** Any clock difference between Identity hosts is subtracted from these limits.
- **Recommended target.** `W − L_max` is never smaller than `SafetyMargin` (at least 1 hour), whatever the lifetimes. A rollout that finishes within `SafetyMargin` is therefore always within the limit, and that remains the recommended target.

**If a rollout exceeds the limit,** the automatic removal would drop the old key while tokens it signed or encrypted can still be valid:

1. **Do not remove the old key.** Keep its entry and file on every instance. Do not restart an instance with the original `RetiredAt` after `RetiredAt + period`: Identity drops a retired key at startup once its period has passed.
2. **Recalculate the earliest safe removal** from the actual last possible issuance time. `T_last` is the moment the last instance with the old key `Active` stopped (or the latest time it could still have issued a token). The earliest safe removal is `T_last + L_max + 30 s`, plus any clock difference between hosts: `T_last + 7 days + 30 s` with the current configuration.
3. **Keep the old key available until then.** Set its `RetiredAt` to `T_last` on every instance. That value lies in the past, so startup accepts it. The automatic removal then moves to `T_last + period`, which is later than the earliest safe removal. This is a configuration change applied at the next rolling restart. Remove the key only after every token signed or encrypted with it has expired.

### 5. Scheduled rollover

1. **Stage.** Add the new signing key as `Pending` and restart every instance (rolling). Wait at least `ValidatorCacheAllowance`.
2. **Activate.**
   - Make the new signing key `Active`, and the old one `Retired` with `RetiredAt` set to the start of this rollout.
   - Rotate the encryption key the same way.
   - Restart every instance (rolling) within the limit of section 4.1: 24 days with the current configuration, and within `SafetyMargin` as the target.
3. **Remove.** After the retention period of section 3, Identity stops loading the old keys. Delete the entries and the files.

**Certificate validity:**

- An Active certificate must be inside its validity period, otherwise startup fails.
- Readiness reports `Degraded` 14 days before the Active signing or encryption certificate expires, and `Unhealthy` after it has expired. Activate the next key before that point.

### 6. Emergency rotation

Unchanged from Phase 2.2 section 6.5:

1. Remove the compromised key at once and add a new `Active` key without a Pending phase. Restart every instance.
2. Every token signed with the removed key then fails: access tokens, ID tokens and refresh tokens. Users sign in again.
3. Validators that see the unknown `kid` refresh their JWKS (I37). The BFF lets that happen at most once per `Bff:Metadata:MinimumRefreshInterval`.
4. A compromised encryption key is rotated the same way. To also end SSO cookies, raise the affected users' security versions (logout-all).

### 7. Early removal fails closed

Removing a retired key before its period has passed never weakens security. Tokens it signed or encrypted are rejected (`invalid_grant`, or `401` at services), and their users sign in again.

### 8. Key store and production gate

- **Unchanged:** only mounted PKCS#12 certificate files are implemented (provider `CertificateFiles`, boundary `IKeyMaterialSource`), and the concrete deployed store stays deferred (D19).
- **Production gate:** before any production-like deployment, either a KMS or HSM provider is implemented, or a certificate-file key-management procedure is explicitly accepted through a decision. That procedure must cover protection, distribution and rotation of the files.

## Consequences

**Positive:**

- A scheduled rollover no longer ends sessions.
- The signing key is deterministic and asserted by test, and a retired key is removed automatically after its period.
- The retention of keys, ledger rows and OpenIddict entries rests on one computed window.

**Negative and accepted:**

- With the current configuration, a retired signing key stays published and accepted for about 32 days.
- The operator must finish an activation rollout within `SafetyMargin`.
- Every state change needs a rolling restart.
- Only certificate files are supported.

**Verification:**

| Status | Evidence |
|---|---|
| Implemented and locally tested | **Identity integration tests** (SQL Server 2022 in Compose, persisted certificates across Identity restarts): `KeyRolloverTests` covers the Active key signing, a Pending key published but never signing, refresh tokens across a rollover, removal, unknown `kid` and tampered signatures, and no key material in logs. **Identity unit tests:** `KeyRingLoaderTests` (window boundary, fail-closed startup) and `HealthCheckTests` (certificate expiry). **BFF integration tests:** `SigningKeyRotationTests` (pre-published key, emergency rotation, unpublished key). |
| Not production-verified | Multi-instance rolling restarts, mounted secrets, and a KMS or HSM provider (none exists). |

## Alternatives considered

- **The ADR-015 R7 and section 6.5 period** (access token lifetime + skew + JWKS cache): it ends every session whose refresh token was signed with the old key (I31). Rejected.
- **The strict per-key bound `L_max`** (7 days today): a shorter exposure, but a second computed bound, and it depends on every client's refresh lifetime staying below its absolute session. Rejected for now. Adopting it later needs a new amendment.
- **Certificate-date selection (V24, V44):** implicit, and an expired certificate can still sign. Rejected (I34).
- **Retiring the encryption key on the long period and dropping the signing key early:** refresh tokens still fail, because they are signed too (I31). Rejected.
