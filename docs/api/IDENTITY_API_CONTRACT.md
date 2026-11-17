# Identity API Contract (Baseline)

- **Status:** **preserved baseline, not a final implementation specification.**
- **Origin:** the Phase 0.3 API design, which is the agreed baseline for the endpoint areas, API conventions and JWT claims.
- **Review:** this contract is re-evaluated in Phase 2 ([PHASE_2_IDENTITY_DESIGN_BRIEF.md](../architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md)) against the selected protocol and provider. **No identity provider has been selected.** Nothing here assumes ASP.NET Core Identity, OpenIddict, Duende IdentityServer, Keycloak or any other product.
- **Implementation:** none. These endpoints do not exist yet.

### How to read this document

| Marker | Meaning |
|---|---|
| **Baseline** | Agreed in Phase 0.3: endpoint areas, conventions and the claim set |
| **Reconstructed** | Exact paths and payload fields written down for this baseline, because the original Phase 0.3 text is not in the repository. **Confirm them against the Phase 0.3 design during Phase 2 review.** |
| **Provider-dependent** | May change or be replaced by standard protocol endpoints, depending on the Phase 2 protocol and provider decision |

---

## 1. Conventions (Baseline)

### 1.1 Base path and versioning

- All Identity endpoints live under **`/api/v1`**.
- Versioning uses a **URL path segment** (`v1`, `v2`, …). A new major version is introduced only for breaking changes. Additive changes stay in the current version.
- Through the Gateway, the service is reached with the `identity` route prefix: `/identity/api/v1/...` → Identity `/api/v1/...` (the Gateway removes the prefix; see [ADR-007](../decisions/ADR-007-yarp-api-gateway.md)). The public routing convention is subject to Phase 2 review.

### 1.2 Resource naming

- Lowercase paths, with **kebab-case** for multi-word segments (for example `logout-all`, `password-reset`).
- Plural nouns for collections (`sessions`). `me` stands for the authenticated user.
- Authentication actions that are not resources are grouped under `/auth`.
- JSON property names use **camelCase**.

### 1.3 Headers

| Header | Direction | Rule |
|---|---|---|
| `Authorization` | Request | `Bearer <access token>` on every authenticated endpoint. Never sent to anonymous endpoints, where it is ignored. |
| `Content-Type` | Request and response | `application/json` for request bodies and success responses. `application/problem+json` for errors. |
| `Accept` | Request | Clients send `application/json`. The server returns `406 Not Acceptable` for unsupported media types. |
| `X-Correlation-ID` | Request and response | Optional on requests. If absent, the server generates one. It is always echoed in the response, written to logs, and propagated to downstream calls and messages. Format: a client-generated opaque string (UUID recommended), length-limited. |
| `Idempotency-Key` | Request | Required **where applicable** (see the endpoint tables). The client generates a unique key per logical operation. A retry with the same key and the same payload returns the original outcome without repeating side effects. The same key with a different payload returns `422`. Retention period: subject to Phase 2 review. |

### 1.4 Errors: RFC 9457 Problem Details

All errors use `application/problem+json`, compatible with [RFC 9457](https://www.rfc-editor.org/rfc/rfc9457):

```json
{
  "type": "https://errors.enterprisecommerce.example/identity/validation-failed",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "The request contains invalid fields.",
  "instance": "POST /api/v1/auth/register",
  "traceId": "00-…",
  "correlationId": "…",
  "errors": {
    "email": ["The email address is not valid."]
  }
}
```

- `type` URIs identify stable error categories. The URI scheme above is **Reconstructed** and is a placeholder domain.
- `errors` appears only for validation failures.
- Responses **never** reveal whether an account exists where that would enable account enumeration (registration, login, email confirmation resend, password reset). Exception details are never returned.

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

## 2. Endpoints

**Paths and fields: Reconstructed. Endpoint areas: Baseline.**

### 2.1 Summary

| Area | Method and path | Auth | Idempotency-Key | Provider-dependent |
|---|---|---|---|---|
| Registration | `POST /api/v1/auth/register` | Anonymous | Required | Partly |
| Login | `POST /api/v1/auth/login` | Anonymous | No | **Yes** (may become a standard token endpoint) |
| Refresh token | `POST /api/v1/auth/refresh` | Refresh token | No (rotation semantics apply) | **Yes** (may become a standard token endpoint) |
| Logout | `POST /api/v1/auth/logout` | Bearer | No (naturally idempotent) | **Yes** (session or end-session semantics) |
| Logout all | `POST /api/v1/auth/logout-all` | Bearer | No (naturally idempotent) | Partly |
| Email confirmation | `POST /api/v1/auth/email-confirmation` | Anonymous (token in body) | No (naturally idempotent) | Partly |
| Email confirmation resend | `POST /api/v1/auth/email-confirmation/resend` | Anonymous | Required | Partly |
| Password reset request | `POST /api/v1/auth/password-reset` | Anonymous | Required | Partly |
| Password reset confirm | `POST /api/v1/auth/password-reset/confirm` | Anonymous (token in body) | Required | Partly |
| Current user | `GET /api/v1/users/me` | Bearer | n/a | Partly (may overlap with an OIDC UserInfo endpoint) |
| List sessions | `GET /api/v1/users/me/sessions` | Bearer | n/a | Partly |
| Revoke session | `DELETE /api/v1/users/me/sessions/{sessionId}` | Bearer | n/a (naturally idempotent) | Partly |

### 2.2 Registration

`POST /api/v1/auth/register`

- **Request:** `email`, `password`, and optional profile fields such as `displayName` (final field list: Phase 2).
- **Response:** `202 Accepted`, with a neutral message that the confirmation instructions have been sent. The response is identical whether or not the email is already registered (enumeration resistance).
- **Notes:**
  - Registration creates an **identity only**. Tenant, merchant and membership creation belong to Tenant & Merchant ([SERVICE_CATALOG.md](../architecture/SERVICE_CATALOG.md)).
  - Whether login is allowed before the email is confirmed is decided in Phase 2.

### 2.3 Login

`POST /api/v1/auth/login` (**Provider-dependent**)

- **Request:** `email` and `password`.
- **Response `200`:** `accessToken`, `tokenType` (`Bearer`), `expiresIn` (seconds), and `refreshToken` (see delivery note).
- **Errors:**
  - `401`, with one generic message for unknown accounts and wrong passwords
  - `429` when rate limited or locked out
- **Notes:**
  - If Phase 2 selects an OAuth 2.0 / OpenID Connect server, interactive login may move to the authorization code + PKCE flow, and token issuance to the provider's standard token endpoint. In that case this endpoint is removed or changes meaning.
  - **Refresh token delivery** (response body versus `HttpOnly` secure cookie for browser clients) is a Phase 2 decision.

### 2.4 Refresh token

`POST /api/v1/auth/refresh` (**Provider-dependent**)

- **Request:** `refreshToken`, or the cookie, depending on the delivery decision.
- **Response `200`:** the same shape as login, including a **new** refresh token (rotation).
- **Errors:** `401` for an invalid, expired, revoked or **reused** refresh token. Reuse revokes the whole token family (Phase 2 token strategy).

### 2.5 Logout

`POST /api/v1/auth/logout`

- Ends the current session and revokes its refresh token(s).
- **Response:** `204 No Content`. It is repeatable: logging out an already ended session also returns `204`.
- Access tokens already issued stay valid until they expire, unless Phase 2 introduces revocation for them (see the [token validation considerations](../architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md#741-token-validation-architecture)).

### 2.6 Logout all

`POST /api/v1/auth/logout-all`

- Ends **all** of the user's sessions and revokes every refresh token. It is also used after a password change or a security event.
- **Response:** `204 No Content`.

### 2.7 Email confirmation

| Endpoint | Request | Response |
|---|---|---|
| `POST /api/v1/auth/email-confirmation` | `userId` or `email`, plus the confirmation `token` | `204` on success. `400` for an invalid or expired token, with a generic message. |
| `POST /api/v1/auth/email-confirmation/resend` | `email` | Always `202`, with a neutral message. |

Confirmation tokens are single-use, time-limited and stored hashed (Phase 2 security design).

### 2.8 Password reset

| Endpoint | Request | Response |
|---|---|---|
| `POST /api/v1/auth/password-reset` | `email` | Always `202`, with a neutral message (enumeration resistance) |
| `POST /api/v1/auth/password-reset/confirm` | `email` or `userId`, the reset `token`, and `newPassword` | `204` on success. `400` for an invalid or expired token, or a password that fails policy. |

A successful reset **invalidates all sessions** (equivalent to logout all).

### 2.9 Current user

`GET /api/v1/users/me`

- **Response `200`:** `id`, `email`, `emailConfirmed`, `displayName`, and `createdAt`. The final field set is decided in Phase 2.
- Returns **identity data only**. Tenant and merchant memberships come from Tenant & Merchant, and platform administrator assignments from Admin.

### 2.10 Session management

| Endpoint | Response |
|---|---|
| `GET /api/v1/users/me/sessions` | `200`: a list of sessions with `id`, `createdAt`, `lastSeenAt`, `current` (boolean), and coarse client information such as user agent and approximate location. Raw IP retention is decided in Phase 2. |
| `DELETE /api/v1/users/me/sessions/{sessionId}` | `204`. Revokes that session's refresh tokens. Returns `404` if the session doesn't belong to the caller (never reveals other users' sessions). |

## 3. JWT access token claims (Baseline)

The proposed claim set is listed below. Claim names and formats are **Provider-dependent** and are confirmed in the Phase 2 token strategy.

| Claim | Meaning | Notes |
|---|---|---|
| `sub` | Subject: the stable, opaque user identifier | Never an email address |
| `iss` | Issuer: the Identity service's issuer URI | Validated by every service |
| `aud` | Audience: the intended API(s) | Validated by every service. Per-service versus shared audience is decided in Phase 2 |
| `exp` | Expiration time | Short-lived access tokens; the lifetime is decided in Phase 2 |
| `iat` | Issued-at time | |
| `jti` | Unique token identifier | Supports auditing and optional denylisting |
| `roles` | Coarse role names | **Not authorization proof on its own.** Platform roles are owned by Admin, and merchant roles by Tenant & Merchant ([ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)) |
| `tenant_context` | Tenant (and possibly merchant) context, **when applicable** | **Never sufficient proof of access.** The owning service validates tenant and resource ownership. Its shape is a Phase 2 decision |
| `amr` | Authentication methods references (for example `pwd`, `mfa`) | Lets services require stronger authentication for sensitive operations |

Rules:

- Tokens carry **no secrets and no unnecessary personal data**.
- Services validate the signature, `iss`, `aud`, `exp` and the algorithm locally ([token validation considerations](../architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md#741-token-validation-architecture)).

## 4. Open items for Phase 2 review

1. Confirm the **Reconstructed** paths and fields against the original Phase 0.3 design.
2. Map every **Provider-dependent** endpoint to the selected protocol and provider. Standard endpoints (for example token, authorize, end-session, discovery, JWKS, UserInfo) may replace custom ones.
3. Refresh token delivery for browser clients: body or `HttpOnly` cookie, and a backend-for-frontend option.
4. Idempotency-Key retention, storage and scope.
5. The `tenant_context` and `roles` claim shape, and the strategy for stale claims.
6. Rate limiting and lockout responses (Gateway versus service).
7. The final error `type` URI scheme.

Out of scope for this baseline: multi-factor authentication enrolment, external logins (for example social providers), administrative user management, and service-to-service credential endpoints. These are evaluated in Phase 2.
