# Testing

## Test projects

| Project | Kind | What it covers |
|---|---|---|
| `EnterpriseCommerce.{Service}.UnitTests` | Unit | Domain rules, application handlers and isolated components, with no I/O |
| `EnterpriseCommerce.{Service}.IntegrationTests` | Integration | The real HTTP pipeline in memory (`WebApplicationFactory`), and later real infrastructure |
| `EnterpriseCommerce.Bff.BrowserTests` | Browser | The BFF flows in Chromium, Firefox and WebKit (Playwright) against Identity and the BFF on real Kestrel listeners |
| `EnterpriseCommerce.Gateway.IntegrationTests` | Integration | The Gateway's edge rules (internal paths are never forwarded) and the committed Development route set |
| `EnterpriseCommerce.ArchitectureTests` | Architecture | Layer dependency direction, forbidden technologies in inner layers, no cross-service references, naming conventions, service ownership (credentials only in Identity, merchant roles never in Identity, no service naming another service's database), and the messaging boundaries of ADR-023 (Confluent.Kafka only in allow-listed Infrastructure projects, no rejected messaging package, messaging types only in those Infrastructure assemblies) |

The TenantMerchant integration tests sign real RS256 `at+jwt` tokens with a test issuer (`TestTokenIssuer`), which serves discovery and the JWKS to the service's own JWT bearer handler. The validation configuration is exercised end to end, without a dependency on the Identity projects.

## Tooling

- **xUnit v3** on **Microsoft.Testing.Platform**. `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }`, so `dotnet test` uses MTP mode.
- `Microsoft.AspNetCore.Mvc.Testing` for in-memory API hosting.
- Architecture tests need no extra package: they parse `.csproj` files and inspect compiled assembly references.

## Running

```bash
dotnet test --solution EnterpriseCommerce.sln -c Release
dotnet test --project tests/ArchitectureTests/EnterpriseCommerce.ArchitectureTests
```

## Infrastructure-backed tests

Infrastructure comes from the Compose profiles; there are no per-test containers. Tests that need infrastructure skip with a reason when it is not configured. With `EC_REQUIRE_INFRA_TESTS=true` (CI) they fail instead, so they can never silently disappear.

| Variable | Needed by | Example (local) |
|---|---|---|
| `EC_TEST_SQLSERVER` | Identity, BFF and TenantMerchant integration tests (throwaway databases, dropped afterwards) | `Server=127.0.0.1,1433;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=true` (`--profile sqlserver`) |
| `EC_TEST_REDIS_SESSIONS` | BFF integration and browser tests (the dedicated `noeviction` session Redis; keys under one prefix per run, deleted afterwards) | `127.0.0.1:6380,password=<REDIS_SESSIONS_PASSWORD>` (`--profile bff`) |
| `EC_TEST_REDIS_RESTART_CONTAINER` | The real Redis restart test (it runs in the serial collection, after the parallel tests) | `enterprisecommerce-redis-sessions-1` |
| `EC_TEST_BROWSERS=true` | Playwright browser tests (they install the browsers on first run) | |

Redis behaviour is never mocked: persistence, `noeviction`, restarts, outages (a TCP forwarder cuts the real connection) and cross-instance refresh concurrency all run against the real session Redis. Fault injection is limited to the BFF's back channel to Identity (lost responses, refused connections, `503`).

```bash
cd deploy/docker && docker compose --profile sqlserver --profile bff up -d --wait && cd ../..
export EC_TEST_SQLSERVER='Server=127.0.0.1,1433;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=true'
export EC_TEST_REDIS_SESSIONS='127.0.0.1:6380,password=<REDIS_SESSIONS_PASSWORD>'
dotnet test --solution EnterpriseCommerce.sln -c Release
EC_TEST_BROWSERS=true dotnet test --project tests/Bff/EnterpriseCommerce.Bff.BrowserTests -c Release
cd deploy/docker && docker compose --profile sqlserver --profile bff down
```

Playwright WebKit is not real Safari. The real Safari gate (ADR-016 section 8) is manual and still open.

## Planned

MongoDB and Kafka integration tests arrive with the first features that use them. Code coverage reporting is also planned.
