# Identity API Contract

- **Status:** **Accepted (Phase 2.2 revision).** It applies [ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) (protocol, tokens, credential entry, routing) and [ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md) (account security tokens and email) to the Phase 0.3 baseline. The endpoint mapping comes from [PHASE_2_2_AUTHENTICATION_FLOWS.md section 8](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#8-endpoint-mapping-proposal).
- **Origin:** the Phase 0.3 API design, the baseline for the endpoint areas, API conventions and JWT claims. [Section 5](#5-changes-from-the-phase-03-baseline) lists what changed.
- **Implementation:** Phase 2.3 implements this contract incrementally. An endpoint exists only once its slice is merged.

### How to read this document

| Marker | Meaning |
|---|---|
| **Baseline** | Agreed in Phase 0.3 and unchanged |
| **Accepted** | Decided by ADR-017 or ADR-018 |
| **Reconstructed** | Exact paths and payload fields written down for the baseline, because the original Phase 0.3 text is not in the repository. Kept unless the implementation needs a change, which is then recorded here. |

---

## 1. Conventions (Baseline)

### 1.1 Base path, host and versioning

- **Accepted:** Identity has **its own host name** per environment (for example `id.<environment domain>`), routed by host through the Gateway **without** a path prefix. Protocol endpoints, discovery, the account pages and the REST API all live on that host. The issuer, the discovery URLs and the URLs used by browsers and services therefore match without rewriting.
- The custom REST endpoints live under **`/api/v1`** on the Identity host.
- The Phase 1 Gateway route with the `/identity` path prefix stays for development diagnostics only. It is never used for issuer or protocol URLs.
- Versioning uses a **URL path segment** (`v1`, `v2`, …). A new major version is introduced only for breaking changes. Additive changes stay in the current version.

### 1.2 Resource naming

- Lowercase paths, with **kebab-case** for multi-word segments (for example `logout-all`).
- Plural nouns for collections (`sessions`). `me` stands for the authenticated user.
- Authentication actions that are not resources are grouped under `/auth`.
- JSON property names use **camelCase**.

### 1.3 Headers

| Header | Direction | Rule |
|---|---|---|
| `Authorization` | Request | `Bearer <access token>` on every authenticated REST endpoint. Never sent to anonymous endpoints, where it is ignored. |
| `Content-Type` | Request and response | `application/json` for request bodies and success responses. `application/problem+json` for errors. Protocol endpoints use the content types required by OAuth 2.0 and OpenID Connect. |
| `Accept` | Request | Clients send `application/json`. The server returns `406 Not Acceptable` for unsupported media types. |
| `X-Correlation-ID` | Request and response | Optional on requests. If absent, the server generates one. It is always echoed in the response, written to logs, and propagated to downstream calls and messages. Format: a client-generated opaque string (UUID recommended), length-limited. |
| `Idempotency-Key` | Request | Required **where applicable** (see the endpoint tables). The client generates a unique key per logical operation. A retry with the same key and the same payload returns the original outcome without repeating side effects. The same key with a different payload returns `422`. Retention period: decided when the first endpoint that needs it is implemented. |

### 1.4 Errors: RFC 9457 Problem Details

REST endpoints return errors as `application/problem+json`, compatible with [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457). Protocol endpoints return OAuth 2.0 error responses (`invalid_grant`, `invalid_client`, …) as their specifications require.

```json
{
  "type": "https://errors.enterprisecommerce.example/identity/validation-failed",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "The request contains invalid fields.",
  "instance": "POST /api/v1/auth/logout-all",
  "traceId": "00-…",
  "correlationId": "…",
  "errors": {
    "field": ["The value is not valid."]
  }
}
```

- `type` URIs identify stable error categories. The URI scheme above is **Reconstructed** and is a placeholder domain.
- `errors` appears only for validation failures.
- Responses **never** reveal whether an account exists where that would enable account enumeration. Exception details are never returned.

Common status codes:

| Code | Use |
|---|---|
| 400 | malformed request, or validation failure |
| 401 | missing, invalid or expired credentials or token |
| 403 | authenticated, but not allowed |
| 404 | resource not found, or not visible to the caller |
| 409 | conflict, for example a concurrent state change |
| 422 | `Idempotency-Key` reused with a different payload |
| 429 | rate limited (`Retry-After` header) |
| 503 | the authoritative security state cannot be confirmed (for example the Identity database is unavailable). No token is issued and no success is reported. |

## 2. Endpoints

### 2.1 Summary

**Accepted** (ADR-017 section 9): every credential entry happens on an **Identity-hosted page**. No client application or BFF ever sends a password to a JSON endpoint.

| Area | Endpoint | Kind | Auth |
|---|---|---|---|
| Discovery | `GET /.well-known/openid-configuration`, JWKS | Protocol | Anonymous |
| Authorization (login) | `GET /connect/authorize` with the Identity-hosted login page | Protocol and page | Identity SSO cookie |
| Token (code exchange, refresh, client credentials) | `POST /connect/token` | Protocol | Client authentication (`private_key_jwt` in deployed environments) |
| Token revocation | `POST /connect/revoke` | Protocol | Client authentication |
| End session | `/connect/endsession` (`id_token_hint`, registered `post_logout_redirect_uri`, `state`): GET confirms, POST signs out | Protocol and page | Identity SSO cookie, antiforgery on POST |
| UserInfo | `/connect/userinfo` | Protocol | Bearer |
| Registration | Identity-hosted registration page | Page (credential entry) | Anonymous, antiforgery |
| Email confirmation | Identity-hosted confirmation page (link target), submitted by POST | Page | Anonymous, antiforgery |
| Email confirmation resend | Identity-hosted page, neutral response | Page | Anonymous, antiforgery |
| Password reset request | Identity-hosted page, neutral response | Page | Anonymous, antiforgery |
| Password reset confirmation | Identity-hosted page (link target), submitted by POST | Page (credential entry) | Anonymous, antiforgery |
| Password change | Identity-hosted page | Page (credential entry) | Identity SSO cookie, antiforgery |
| Identity sign-out | Identity-hosted page, POST only, local return URL | Page | Identity SSO cookie, antiforgery |
| Logout all | `POST /api/v1/auth/logout-all` | REST | Bearer |
| Current user | `GET /api/v1/users/me` | REST | Bearer |
| List sessions | `GET /api/v1/users/me/sessions` | REST | Bearer |
| Revoke session | `DELETE /api/v1/users/me/sessions/{sessionId}` | REST | Bearer |
| Account disable and reactivate | `POST /internal/v1/accounts/{accountId}/disable`, `POST /internal/v1/accounts/{accountId}/reactivate` ([section 2.12](#212-account-disablement-and-reactivation-internal)) | REST (internal) | Service token only (allow-listed `svc-` client, scope `svc.identity.account-lifecycle`) |
| Account email verification | `POST /internal/v1/accounts/{accountId}/email-verification` ([section 2.13](#213-account-email-verification-internal)) | REST (internal) | Service token only (allow-listed `svc-` client, scope `svc.identity.account-verification`) |

Page paths are implementation details of Identity.Api and are not a client contract. Clients reach the pages through `/connect/authorize` or through links that Identity issues.

### 2.2 Registration (page)

**Implemented (Phase 2.3 S3).** Page: `/account/register`.

- **Fields:** email, password, password repeated. Profile fields are not collected yet.
- **Server-side validation** (shown on the page): a plain email address (no display name, at most 256 characters, normalized upper-invariant for uniqueness) and the ASP.NET Core Identity password policy (at least 12 characters, upper case, lower case, digit, symbol). These checks never depend on whether the account exists.
- **Outcome:** a redirect to the neutral page `/account/check-email` in every accepted case, whether or not the email is already registered (enumeration resistance).
  - New address: one authoritative transaction creates the account (email unconfirmed), its security-version record (version 1) and a queued confirmation delivery. The user is **not** signed in.
  - Existing, still unconfirmed account: nothing is created. A confirmation email is requested again within the resend limits.
  - Existing, confirmed or disabled account: nothing is created and nothing is sent. The "someone tried to register with your address" notice is not implemented yet.
- Registration creates an **identity only**. Tenant, merchant and membership creation belong to Tenant & Merchant ([SERVICE_CATALOG.md](../architecture/SERVICE_CATALOG.md)).
- An account cannot sign in before its email is confirmed.

### 2.3 Login and token issuance (protocol)

- Interactive login is the authorization code flow with PKCE `S256` through `/connect/authorize`. The user enters credentials only on the Identity-hosted login page.
- The BFF exchanges the code at `/connect/token`. The response contains the access token, the ID token and, when `offline_access` was granted, a refresh token. Tokens never reach browser JavaScript ([ADR-016](../decisions/ADR-016-browser-client-architecture-backend-for-frontend.md)).
- **Errors on the login page:** one generic message for unknown accounts, wrong passwords, locked accounts and disabled accounts. "Confirm your email" is shown only after a correct password. Rate limiting returns `429` with `Retry-After`.

### 2.4 Refresh (protocol)

- `POST /connect/token` with `grant_type=refresh_token` and client authentication.
- Every refresh returns a **new** refresh token (rotation). There is no reuse leeway.
- `invalid_grant` for an invalid, expired, revoked or **reused** refresh token, for a stale security version, and for a token presented by a client other than the one it was issued to. A reuse revokes the affected session (for the admin console, every session of the user). A presenter mismatch revokes nothing.
- `503` when the security state or the redemption ledger cannot be confirmed. Nothing is revoked, and the caller must treat the outcome as unknown and never retry with the same refresh token.

### 2.5 Logout (protocol)

**Implemented (Phase 2.3 S4).** Browser logout (through a BFF) and the bearer-token logout-all of section 2.6 are separate operations: logout ends **one** client session and the browser's Identity SSO session; logout-all ends **every** session of the user.

**Token revocation, `POST /connect/revoke`** (RFC 7009, client authentication as at the token endpoint):

- Revoking a refresh or access token also revokes the **authorization** it belongs to (the client session), in the authoritative transaction. The authorization's other tokens are revoked afterwards as cleanup. Other sessions of the user are untouched.
- `200` for a revoked, already revoked or unknown token, so the call is repeatable.
- A token issued to **another client** is not revoked and its session is untouched. OpenIddict still answers `200`, which reveals nothing about the token.
- `503` when the authorization status cannot be committed. Then nothing is revoked, not even the presented token. Per PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.5.4 the BFF still ends its own session.

**End session, `/connect/endsession`** (OpenID Connect RP-initiated logout):

- **GET never changes state.** It shows an Identity page with a "Sign out" button that submits the validated parameters by POST. A link, image or redirect alone can never sign a user out.
- **POST** requires the antiforgery token and passes the Fetch Metadata and exact `Origin` checks. A cross-origin POST gets `403`, and a POST without antiforgery `400`.
- `post_logout_redirect_uri` must exactly match a URI registered for the client (`Identity:Clients:n:PostLogoutRedirectUris`). Any other value, including another client's URI or an external URL, is rejected with `400` and nothing is changed. Without one, the browser goes to the local page `/account/signed-out`. `state` is returned unchanged.
- If `id_token_hint` names a session (authorization) of the **user signed in with the current SSO cookie**, that session is revoked first, in the authoritative transaction. A hint for another user, or without a signed-in user, revokes nothing.
- The SSO cookie is cleared **only after** that revocation has committed. If it cannot be committed, the response is `503` and the cookie is kept.
- Repeatable: an already ended session also signs out successfully.

**Identity sign-out, `/account/logout`:** for users on Identity pages without a client application. GET renders the form, and POST clears the SSO cookie and redirects to `returnUrl` only if it is a local URL, otherwise to `/account/signed-out`.

Access tokens already issued stay valid until they expire (ADR-017 section 6). The BFF discards its copies at logout.

### 2.6 Logout all

**Implemented (Phase 2.3 S4).** `POST /api/v1/auth/logout-all`

- **Authentication:** `Authorization: Bearer <access token>` only. Identity validates the token locally like every other service (signature, issuer, `typ: at+jwt`, lifetime), and requires one of the **front-end audiences** of its browser clients (`ec-storefront`, `ec-merchant`, `ec-admin`), `ec_ptype=user` and a `sub`. The Identity SSO cookie is never accepted here, so the endpoint cannot be triggered by CSRF.
- The user is the token's `sub`. No request value selects whose sessions end.
- Raises the user's security version in the authoritative transaction, so no session of the user can refresh, exchange a code or pass cookie validation afterwards. Every authorization and token of the user is then revoked as cleanup. A cleanup failure does not change the response: the security version already ends every session.
- **Responses:**
  - `204 No Content`, with no body and no session details. A repeated call also returns `204`.
  - `401` without a valid token, including tokens for other audiences (service tokens).
  - `403` when the token is not a user token.
  - `503` if the security version could not be raised. Logout-all never reports success without the committed change.
- A refresh racing logout-all either commits first, in which case its successor refresh token is then unusable, or is refused. It never issues tokens from the replaced security version (PHASE_2_2_AUTHENTICATION_FLOWS.md section 11.7, I13).

### 2.7 Email confirmation

**Implemented (Phase 2.3 S3).** Identity-hosted pages ([ADR-018](../decisions/ADR-018-account-security-tokens-and-security-email-delivery.md)).

| Page | Behaviour |
|---|---|
| `GET /account/confirm-email?token=...` | Renders a form with a "Confirm my email address" button. **Never consumes the token**, so email scanners and link previews change nothing. |
| `POST /account/confirm-email` | Consumes the token and confirms the email in one authoritative transaction. Success: "Your email address is confirmed". Every failure (invalid, expired, superseded, consumed, account not active) shows one generic message with a link to request a new email. The token is never echoed back. |
| `GET/POST /account/resend-confirmation` | Takes an email address. Always redirects to `/account/check-email`. |

- The link carries only a 256-bit random token (URL-safe Base64, 43 characters), never the user ID or the email address. Only its SHA-256 hash is stored. The token is looked up by hash; no request parameter selects the account.
- Token lifetime: **24 hours**. Only one unconsumed confirmation token exists per account: minting a new one supersedes the earlier ones, and a successful confirmation supersedes any remaining one.
- The token is minted only when the delivery worker claims the delivery, so a delivery that is never dispatched never creates a token.
- **Resend limits** per account and purpose: at most 3 emails per hour, at most 10 per day, and at least 60 seconds between two. Excess requests are suppressed silently: nothing is recorded, sent or minted, and the response is unchanged. The limits are enforced in the database under a per-account row lock, so they hold across Identity instances.
- Unknown, confirmed and disabled addresses get the same response and no email.
- All `/account/*` responses send `Cache-Control: no-store`, `Referrer-Policy: no-referrer` and `X-Content-Type-Options: nosniff`. POST requests also require the antiforgery token and pass the Fetch Metadata and exact `Origin` checks (ADR-016 section 6).
- Rate limits: per IP in Identity (`Identity:RateLimits:CredentialRequestsPerMinute`) and at the Gateway for routes with the `identity-account` policy (`Gateway:RateLimits:IdentityAccountRequestsPerMinute`, default 30 per minute).

### 2.8 Password reset (pages)

**Implemented (Phase 2.3 S4).**

| Page | Behaviour |
|---|---|
| `GET/POST /account/forgot-password` | Takes an email address and always redirects to `/account/reset-requested`. A confirmed, active account is sent a reset link, an unconfirmed, active account a confirmation link instead, and an unknown or disabled address nothing. The resend limits of section 2.7 apply per account and purpose, silently. |
| `GET /account/reset-password?token=...` | Renders the new-password form. **Never consumes the token.** |
| `POST /account/reset-password` | Resets the password. Success: "Your password has been changed", and the user is **not** signed in. Every token failure (invalid, expired, superseded, consumed, account not active) shows one generic message with a link to request a new one, and the token is not echoed back. A password-policy failure shows the policy and keeps the still unconsumed token in the form. |

- **Token:** as in section 2.7 (256 bits, only the SHA-256 hash stored, looked up by hash), purpose `PasswordReset`, lifetime **30 minutes** (`Identity:SecurityEmail:PasswordResetTokenLifetime`, at most one hour). It is minted when the delivery worker claims the delivery, and only while the account is still active and confirmed.
- **One authoritative transaction:**
  1. consume the token
  2. set the new password hash (which also rotates the ASP.NET Core Identity security stamp and clears the lockout)
  3. raise the security version
  4. supersede every other outstanding security token of the user

  Any failure rolls all of it back, and the token stays usable until it expires. A missing security-version record fails closed with `503`. The password policy is checked and the hash computed before the transaction.
- After commit, every authorization and token of the user is revoked as cleanup. Every session of the user ends: refresh chains, BFF sessions at their next refresh, and SSO cookies in every browser.
- Same headers, antiforgery, Fetch Metadata, `Origin` and rate limits as section 2.7.

### 2.9 Password change (page)

**Implemented (Phase 2.3 S4).** `GET/POST /account/change-password`, signed in with the Identity SSO cookie (otherwise a redirect to the login page).

- The account is the one of the SSO cookie, never a request value. The current password is required. A wrong one counts towards lockout and shows one generic message.
- **One authoritative transaction:** set the new hash only while the stored hash is still the one just verified (so of two concurrent changes exactly one wins), raise the security version, and supersede outstanding reset tokens. After commit, every authorization and token of the user is revoked as cleanup.
- **The current browser stays signed in:** after commit, and only then, its SSO cookie is reissued with the new security version and the original sign-in time. The old cookie is rejected from then on.
- **Client sessions (ADR-017 section 7):** raising the version ends every BFF session of the user, **including the one in the same browser**, because a BFF session is not yet linked to the SSO cookie. That BFF gets `invalid_grant` at its next refresh and can authorize again through the reissued SSO cookie without a password prompt.
- If reissuing the cookie fails after commit, the change stands, the browser is signed out, and the page asks the user to sign in again.
- `503` if the change cannot be committed. The password and every session are then unchanged.

### 2.10 Current user

`GET /api/v1/users/me`

- **Response `200`:** `id`, `email`, `emailConfirmed`, `displayName`, and `createdAt`. The final field set is decided in the implementation.
- Returns **identity data only**. Tenant and merchant memberships come from Tenant & Merchant, and platform administrator assignments from Admin.

### 2.11 Session management

| Endpoint | Response |
|---|---|
| `GET /api/v1/users/me/sessions` | `200`: the caller's valid sessions (one per login per client) with an opaque `id`, `createdAt`, `lastSeenAt`, `current` (boolean), and coarse client information. Raw IP retention is decided with the data model. |
| `DELETE /api/v1/users/me/sessions/{sessionId}` | `204`. Revokes that session's authorization and tokens. Returns `404` if the session doesn't belong to the caller (never reveals other users' sessions). |

### 2.12 Account disablement and reactivation (internal)

**Implemented (Phase 2.3 S6).** Called by Admin, which authorizes its platform administrators first ([PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.6.6](../architecture/PHASE_2_2_AUTHENTICATION_FLOWS.md#566-account-disablement-and-reactivation)). The Gateway never routes `/internal/**`; the endpoints are reachable on the internal network only. Behaviour, audit and concurrency: [IDENTITY_OPERATIONS.md section 1](../security/IDENTITY_OPERATIONS.md#1-account-disablement-and-reactivation).

| Endpoint | Effect |
|---|---|
| `POST /internal/v1/accounts/{accountId}/disable` | In one authoritative transaction: status `Disabled`, new security stamp, security version + 1, every outstanding security token superseded. Afterwards, every authorization and token of the user is revoked as cleanup. |
| `POST /internal/v1/accounts/{accountId}/reactivate` | Status `Active`. The security version is unchanged, and nothing revoked earlier becomes valid again. |

- **Authentication:** `Authorization: Bearer <access token>` from the client credentials grant. Identity accepts the token only if all of these hold:
  - `ec_ptype=service`
  - the audience `Identity:AccountAdministration:Audience` (default `identity-api`)
  - the scope `svc.identity.account-lifecycle`
  - a `client_id` that has the `svc-` prefix and is listed in `Identity:AccountAdministration:AllowedClientIds`

  An empty list denies every caller. Identity authorizes the calling client, never an administrator role.
- `accountId` is the user's `sub`.
- **Body (JSON):**

  | Field | Rule |
  |---|---|
  | `reason` | One of `Administrative`, `PolicyViolation`, `SecurityConcern`, `UserRequest` |
  | `actorReference` | Admin's opaque reference to the administrator: 1 to 128 characters of letters, digits, `.`, `_`, `:` or `-`. Recorded for audit only; never authorizes anything. |

- **Idempotent:** a repeated request returns `changed: false`. A repeated disable also re-runs the post-commit cleanup. No `Idempotency-Key` is needed.
- **Responses:**

  | Code | Meaning |
  |---|---|
  | `200` | `{ "accountId": "...", "status": "Disabled" \| "Active", "changed": true \| false }` |
  | `400` | Validation problem details (`errors` per field) |
  | `401` | No valid token for Identity: no token, or another service's audience |
  | `403` | A valid token that is not an allowed service caller, for example a user token |
  | `404` | Unknown account. The caller is a trusted service, so this is no enumeration risk. |
  | `503` | The change could not be committed. Nothing changed. |

### 2.13 Account email verification (internal)

**Implemented (Phase 3 S2, [ADR-021](../decisions/ADR-021-merchant-invitations-and-membership-administration.md)).** Called by Tenant & Merchant before it accepts a merchant invitation (decision D14). The question is: *is this the verified email address of this active account?* The Gateway never routes `/internal/**`.

`POST /internal/v1/accounts/{accountId}/email-verification` with `{ "email": "new.hire@example.test" }`.

- **Authentication:** `Authorization: Bearer <access token>` from the client credentials grant. Identity accepts the token only if all of these hold:
  - `ec_ptype=service`
  - the audience `Identity:AccountAdministration:Audience` (default `identity-api`)
  - the scope `svc.identity.account-verification`, which is separate from the lifecycle scope: neither grants the other
  - `sub == client_id`, with the `svc-` prefix, listed in `Identity:AccountVerification:AllowedClientIds`

  The list is empty by default, which denies every caller. Startup refuses an entry that is not a service client.
- **Request:**
  - `accountId` is the user's `sub`.
  - `email` must be a plain address that passes Identity's account email rules; it is normalized the same way as stored addresses.
  - Any other member answers `400`.
- **Data minimization:** the caller sends the address it holds, and Identity answers with a verdict. **The account's own address is never returned, and the address is never logged.**
- **Decision order:**
  1. Unknown account: `404`.
  2. Not `Active`: `Inactive`.
  3. Email not confirmed: `Unverified`.
  4. A different normalized address: `Mismatch`.
  5. Otherwise: `Match`.
- **Responses:**

  | Code | Meaning |
  |---|---|
  | `200` | `{ "accountId": "...", "result": "Match" \| "Mismatch" \| "Unverified" \| "Inactive" }` |
  | `400` | Validation problem details (`email`, `accountId`), or a malformed body or unknown member |
  | `401` | No valid token for Identity |
  | `403` | A valid token that is not an allowed verification caller: a user token, a token with only the lifecycle scope, or a client that is not allow-listed |
  | `404` | Unknown account. The caller is a trusted service, so this is no enumeration risk. |
  | `503` | The database is unavailable. No verdict is given, and the caller must fail closed. |

- **Read only.** It changes nothing and needs no `Idempotency-Key`.
- **Caller registration:** configuration only ([IDENTITY_OPERATIONS.md section 1.3](../security/IDENTITY_OPERATIONS.md#13-email-verification-for-tenant--merchant)).

<a id="3-jwt-access-token-claims-baseline"></a>

## 3. JWT access token claims (Accepted)

Access tokens are RS256-signed JWTs with `typ: at+jwt` in the header ([ADR-017](../decisions/ADR-017-token-session-and-protocol-strategy.md) section 2).

| Claim | Meaning | Notes |
|---|---|---|
| `iss` | Issuer: the Identity service's public issuer URI | Exact match in every service |
| `sub` | Subject: the stable, opaque user identifier. For service tokens, the client ID. | Never an email address |
| `aud` | Audience | User tokens: the front end's audience (`ec-storefront`, `ec-merchant` or `ec-admin`). Service tokens: the called service. |
| `exp`, `iat`, `nbf` | Validity | Short-lived. Validated with at most 30 seconds of clock skew. |
| `jti` | Unique token identifier | Auditing, and an optional denylist later |
| `client_id` | The client the token was issued to | `svc-` prefixed IDs are service clients |
| `scope` | Granted scopes | |
| `amr` | Authentication methods references (`pwd`, later `mfa`) | Present when known |
| `ec_ptype` | Principal type: `user` or `service` | |

**Not included:** `roles`, `tenant_context`, email, name or other personal data. Platform roles are owned by Admin, and merchant roles by Tenant & Merchant. The tenant is an explicit request parameter validated by the owning service ([ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)).

Rules:

- Tokens carry **no secrets and no unnecessary personal data**.
- Services validate the signature, `iss`, `aud`, `exp`, `nbf`, the algorithm (`RS256` only) and `typ` locally, with the standard ASP.NET Core JWT bearer handler.
- Services never depend on OpenIddict-internal `oi_*` claims.

<a id="4-open-items-for-phase-2-review"></a>

## 4. Remaining open items

1. Confirm the **Reconstructed** REST fields (`users/me`, sessions) during implementation.
2. `Idempotency-Key` retention, storage and scope, decided with the first endpoint that requires it.
3. The final error `type` URI scheme.
4. ~~Internal Admin endpoints for account disablement and reactivation~~: resolved in Phase 2.3 S6 ([section 2.12](#212-account-disablement-and-reactivation-internal)).

Out of scope for this contract: MFA enrolment and external logins (social providers).

## 4a. Outbound: security email handover to Notification (internal)

**Defined by Identity in Phase 2.3 S3. Not yet implemented by Notification.** Identity calls it from its delivery worker (ADR-018 section 3). Until Notification provides it, Identity cannot run outside Development.

`POST {Notification internal base address}/internal/v1/security-emails`

| Part | Content |
|---|---|
| Authentication | `Authorization: Bearer <access token>`. Identity obtains it with the client credentials grant as the service client `svc-identity` (default), scope `svc.notification.security-email`, authenticating with `private_key_jwt` (typed `client-authentication+jwt`). |
| `Idempotency-Key` | The delivery ID (a GUID). Retries of one delivery always reuse it, with the same link. Notification must treat a repeated key as the same email and answer `2xx` or `409`. |
| Body (JSON) | `deliveryId` (GUID), `purpose` (`EmailConfirmation` or `PasswordReset`), `recipient` (email address), `link` (absolute URL that contains the token). |
| Responses | `2xx` or `409`: accepted. `401`, `408`, `429`, `5xx`: transient, retried. Other `4xx`: rejected, not retried. A timeout or broken connection after sending is an unknown outcome and is retried with the same key. |
| Notification obligations | Never log, persist or forward the link or the rendered body of a security email beyond delivery (PHASE_2_2_AUTHENTICATION_FLOWS.md section 5.6.4). Authorize the calling client and scope. |

Identity configuration: `Identity:Notification:BaseAddress`, `TokenEndpoint`, `Issuer`, `ClientId`, `PrivateKeyPath`, `Scope`, `Timeout`. The private key comes from a protected store and is never committed.

## 5. Changes from the Phase 0.3 baseline

| Baseline endpoint | Now | Reason |
|---|---|---|
| `POST /api/v1/auth/register` | Identity-hosted registration page | Credential entry only on Identity pages (ADR-017 section 9) |
| `POST /api/v1/auth/login` | `/connect/authorize` with the Identity login page, then `/connect/token` | OAuth 2.0 / OpenID Connect (ADR-015, ADR-017) |
| `POST /api/v1/auth/refresh` | `/connect/token` with `grant_type=refresh_token` | Standard protocol endpoint |
| `POST /api/v1/auth/logout` | `/connect/revoke` plus `/connect/endsession` | Standard protocol endpoints, with custom session revocation |
| `POST /api/v1/auth/email-confirmation` and `/resend` | Identity-hosted pages, token consumed only by POST | ADR-018 |
| `POST /api/v1/auth/password-reset` and `/confirm` | Identity-hosted pages | Credential entry only on Identity pages, ADR-018 |
| `POST /api/v1/auth/logout-all`, `GET /api/v1/users/me`, sessions | Kept | Custom REST |
| `/identity/api/v1/...` through the Gateway prefix | A dedicated Identity host, routed by host | Issuer and URL consistency (ADR-017 section 10) |
| Claims `roles` and `tenant_context` | Removed | ADR-017 section 2, ADR-014 |
