# Security

Security design documents go here.

Current baseline (Phase 1):

- No secrets in source control. Local infrastructure credentials come from `deploy/docker/.env`, which is gitignored. `.env.example` holds documented, local-only placeholders.
- Local infrastructure ports bind to `127.0.0.1` only.
- Unhandled exceptions return a generic problem details response. Exception messages and stack traces are never sent to clients.
- OpenAPI documents are exposed only in the Development environment.
- NuGet vulnerability auditing (`NuGetAudit`, all packages, including transitive) runs on every restore, and warnings fail the build.
- **Payments are simulated. Card data is never collected, transmitted or stored.**

Open topics for dedicated design steps: identity protocol and provider, token strategy, service-to-service authentication, tenant isolation enforcement, secret management outside local development, and TLS.
