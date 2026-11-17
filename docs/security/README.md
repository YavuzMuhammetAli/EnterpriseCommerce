# Security

Security design documents go here.

Current baseline (Phase 1):

- No secrets in source control. Local infrastructure credentials come from `deploy/docker/.env`, which is gitignored. `.env.example` holds documented, local-only placeholders.
- Local infrastructure ports bind to `127.0.0.1` only.
- Unhandled exceptions return a generic problem details response. Exception messages and stack traces are never sent to clients.
- OpenAPI documents are exposed only in the Development environment.
- NuGet vulnerability auditing (`NuGetAudit`, all packages, including transitive) runs on every restore, and warnings fail the build.
- **Payments are simulated. Card data is never collected, transmitted or stored.**

Accepted security principles ([PROJECT_DECISIONS.md section 10](../architecture/PROJECT_DECISIONS.md#10-security), [ADR-014](../decisions/ADR-014-logical-tenant-isolation.md)):

- Service-level authorization is mandatory, and gateway authorization never replaces it.
- A tenant ID or token claim is not, by itself, proof of access rights.
- Platform administration and merchant authorization are separate concerns.
- Sensitive information is never published in integration events.

Open topics for dedicated design steps (the Identity topics are scoped in [PHASE_2_IDENTITY_DESIGN_BRIEF.md](../architecture/PHASE_2_IDENTITY_DESIGN_BRIEF.md)): identity protocol and provider, token strategy, service-to-service authentication, tenant isolation enforcement, secret management outside local development, and TLS.
