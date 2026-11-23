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

## Known test-stability issues

Known issues stay visible: a run with such a failure is reported as failed, and reruns are reported as additional evidence, never as a pass. Assertions are not weakened, timeouts are not raised without a demonstrated cause, and failures are not retried automatically or hidden.

### TS-1: Chromium sign-in timeout under the full parallel run (open)

| | |
|---|---|
| Test | `EnterpriseCommerce.Bff.BrowserTests.BrowserFlowTests.AfterAPasswordChange_TheBffReauthorizesSilently(engine: "chromium")` |
| Failing call | `BrowserEnvironment.SignInAsync` ([BrowserEnvironment.cs](../../tests/Bff/EnterpriseCommerce.Bff.BrowserTests/BrowserEnvironment.cs), the final `WaitForURLAsync`), called from the test's first step (`BrowserFlowTests.cs` line 172), **before** the password change it tests |
| Symptom | `System.TimeoutException: Timeout 30000ms exceeded` from Playwright's `Frame.WaitForLoadStateAsync`, waiting for the redirect from Identity's login form back to the BFF landing page. Playwright's default timeout; the test sets none. |
| Occurrences | Local full-solution runs only, with every suite running in parallel: 2026-11-21 (Phase 3 S3 run) and 2026-11-23 (Phase 3 S4 step 1 run, 1114 tests, the only failure). Chromium only. |
| Not reproduced | The browser suite run on its own passed 18 of 18 three times on 2026-11-21 and twice on 2026-11-23. CI results up to 2026-11-23 are **not evidence** either way: the browser test step could not fail on a test failure ([CI-1](#ci-1-test-steps-could-not-fail-on-a-test-failure-fixed-2026-11-23)). |
| Assessment | Load-sensitive: the same steps succeed without concurrent suites. Both occurrences came from runs that changed no BFF or Identity code. The root cause (for example CPU starvation of the headless browser or of the in-process Identity and BFF hosts under the parallel load) is **not demonstrated**. |
| Policy until resolved | Report it; never treat it as a pass. Do not raise the timeout or add retries without evidence. |
| Next step | A dedicated investigation: capture a Playwright trace and host timings on failure, and measure the sign-in redirect chain under the parallel load. Then fix the demonstrated cause, for example by isolating the browser suite from the parallel run if starvation is shown. |

## CI-1: test steps could not fail on a test failure (fixed 2026-11-23)

**Defect.** Both CI test steps piped their output into `tee` (`dotnet test ... 2>&1 | tee test-output.log`) and had no `shell:` key. GitHub runs such a step as `bash -e {0}`, **without `pipefail`**. Only an explicit `shell: bash` runs `bash --noprofile --norc -eo pipefail {0}`. The step's exit status was therefore `tee`'s, and a failing test could not fail the step. The workflow comment claimed the opposite.

**Consequence.** A green CI run before the fix shows that the solution restored and built, and that the publish and secret checks passed. **It does not show that the tests passed.** Earlier statements that tests passed in CI rest on that status alone. GitHub's per-test logs need authentication and were not reviewed here; the repository owner can read them in the Actions UI.

**A failure it hid.** `DevelopmentAdapterTests.ReleaseBuild_IdentityApi_DoesNotReferenceOrShipTheMailSink` fails under CI's command sequence:
- The sequence was a configuration-less `dotnet restore` (which evaluates as Debug), then `dotnet build -c Release --no-restore`.
- The restore graph then contains Identity.Api's Debug-only `DevMailSink` reference, and the Release build copies the mail sink into test outputs. The Identity publish output stays clean (the separate publish check).
- This was reproduced in the `mcr.microsoft.com/dotnet/sdk:10.0.301` and `10.0.404` Linux images, on a clean export of the commit, and on Windows, on both the S3 baseline and S4 step 1.

**Fix.**
- The restore runs with `-p:Configuration=Release`.
- Both test steps set `shell: bash`.
- Validated in the 10.0.301 Linux image with CI's sequence: no mail sink in the assets or outputs, and 97 of 97 architecture tests passing, with `pipefail` on.

**Locally:** restore with `-p:Configuration=Release`, or let `dotnet build` and `dotnet test -c Release` restore.

## Planned

MongoDB and Kafka integration tests arrive with the first features that use them. Code coverage reporting is also planned.
