# Testing

## Test projects

| Project | Kind | What it covers |
|---|---|---|
| `EnterpriseCommerce.{Service}.UnitTests` | Unit | Domain rules, application handlers and isolated components, with no I/O |
| `EnterpriseCommerce.{Service}.IntegrationTests` | Integration | The real HTTP pipeline in memory (`WebApplicationFactory`), and later real infrastructure |
| `EnterpriseCommerce.Bff.BrowserTests` | Browser | The BFF flows in Chromium, Firefox and WebKit (Playwright) against Identity and the BFF on real Kestrel listeners |
| `EnterpriseCommerce.Gateway.IntegrationTests` | Integration | The Gateway's edge rules (internal paths are never forwarded) and the committed Development route set |
| `EnterpriseCommerce.ArchitectureTests` | Architecture | Layer dependency direction, forbidden technologies in inner layers, no cross-service references, naming conventions, service ownership (credentials only in Identity, merchant roles never in Identity, no service naming another service's database), and the messaging boundaries of ADR-023 and ADR-028 (Confluent.Kafka declared only by `BuildingBlocks.Messaging`, the block referenced only by Infrastructure projects, no rejected messaging package, messaging types only in the block and in allow-listed publishers' Infrastructure) |

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
| `EC_TEST_SQLSERVER` | Identity, BFF, TenantMerchant and Inventory integration tests (throwaway databases, dropped afterwards) | `Server=127.0.0.1,1433;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=true` (`--profile sqlserver`) |
| `EC_TEST_REDIS_SESSIONS` | BFF integration and browser tests (the dedicated `noeviction` session Redis; keys under one prefix per run, deleted afterwards) | `127.0.0.1:6380,password=<REDIS_SESSIONS_PASSWORD>` (`--profile bff`) |
| `EC_TEST_REDIS_RESTART_CONTAINER` | The real Redis restart test (it runs in the serial collection, after the parallel tests) | `enterprisecommerce-redis-sessions-1` |
| `EC_TEST_BROWSERS=true` | Playwright browser tests (they install the browsers on first run) | |
| `EC_TEST_KAFKA` | TenantMerchant and Inventory Kafka tests of the outbox relay (one topic per test, created with AdminClient and deleted afterwards) | `127.0.0.1:9094` (`--profile kafka`) |
| `EC_TEST_KAFKA_RESTART_CONTAINER` | The real Kafka outage test (it stops and starts the broker; serial collection, after the parallel tests) | `enterprisecommerce-kafka-1` (`kafka` in CI) |
| `EC_TEST_MEASURE=true` | The opt-in Inventory measurements (`InventoryMeasurements`); never in CI. `EC_TEST_MEASURE_OUTPUT` names a file the result rows are appended to. | |
| `EC_TEST_MONGODB` | Catalog integration tests (one throwaway database per test fixture, `ec_catalog_test_<guid>`, dropped afterwards) | `mongodb://localdev:<MONGO_ROOT_PASSWORD>@127.0.0.1:27018/?authSource=admin` (`MONGO_PORT=27018 docker compose --profile mongodb up -d --wait`) |

Kafka behaviour is not mocked where it matters either. The TenantMerchant outbox relay's Kafka tests publish to a real broker:
- end to end from both creation endpoints
- per-tenant order and partition spread
- dual-fact identity
- redelivery after a simulated crash between acknowledgement and marking, with identical bytes
- recovery by a new relay, and graceful shutdown
- a missing topic
- a real outage: the broker's container is stopped and started

The relay's decision logic (backoff, the halted state, prefix marking) is also unit-tested against fakes of its store, producer and leadership. Its SQL Server behaviour (committed-only reads, leadership and takeover after a killed session) runs against the real database.

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

## Browser tests: isolation and engine limitations

**Isolation.** Every browser test gets its own Playwright driver (the Node process) and browser, released when its context closes (`BrowserEnvironment.NewContextAsync`); the fixture disposes any driver left over.
- A crash of the driver process therefore ends only the test that caused it. With one shared driver, one WebKit crash failed every later test of every engine (TS-2).
- The cost is one driver start per test, measured on 2026-11-23 with Playwright 1.64.1 (median of 10 runs; `CreateAsync` plus `Dispose`):
  - Linux, in the official image: 360 + 7 ms
  - Windows: 407 + 17 ms

  That adds about 7 to 8 seconds per run of the 18 browser tests.

**WebKit does not report the `Set-Cookie` headers of redirect responses**, on any operating system.
- A probe with Playwright 1.64.1 (WebKit 2370, `mcr.microsoft.com/playwright/dotnet:v1.64.1-noble`, two runs) checked a 200 response and four 302 responses:
  - a same-origin redirect
  - a form POST answered with 302
  - a cross-site redirect chain
  - a cross-site `form_post` answered with 302
- It found the header only on the 200 response, through both `HeadersArrayAsync` and `AllHeadersAsync`.
- The cookie store kept every cookie with Secure, HttpOnly and SameSite=Strict. Chromium and Firefox reported the header in every case.
- The BFF sets its cookies on redirects (`/bff/login` and `/bff/callback` answer 302). So `Login_SetsTheAcceptedCookies_AndNoTokenReachesTheBrowser` handles the engines as follows:

| Assertion | Chromium, Firefox | WebKit |
|---|---|---|
| Cookie store: the session cookie exists, Secure, HttpOnly, SameSite=Strict, `Path=/`, no expiry, no leftover OIDC cookies; the correlation cookies are Secure, HttpOnly, SameSite=Lax, `Path=/` | required | required. Exception: SameSite on **Windows** WebKit, whose cookie store reports None for every cookie |
| Raw `Set-Cookie` of the session cookie, with `samesite=strict` | required | checked if reported (it is not) |
| Exactly two raw correlation `Set-Cookie` headers, each with `samesite=lax` | required | checked if reported (they are not) |

`LoginFlowTests` (integration tests) verify every `Set-Cookie` attribute on the wire, independently of any browser.

**Mutation checks (2026-11-23, Linux image):**
- Expecting SameSite=Lax for the session cookie fails the cookie-store assertion on Chromium, Firefox and WebKit.
- Expecting `samesite=lax` in its raw header fails on Chromium and Firefox.
- Expecting three correlation headers fails on Chromium and Firefox, which report exactly two.

## Known test-stability issues

Known issues stay visible: a run with such a failure is reported as failed, and reruns are reported as additional evidence, never as a pass. Assertions are not weakened, timeouts are not raised without a demonstrated cause, and failures are not retried automatically or hidden.

### TS-1: 30-second navigation timeouts after a form submission (open)

| | |
|---|---|
| Tests | `BrowserFlowTests.AfterAPasswordChange_TheBffReauthorizesSilently` (Chromium, WebKit) and `BrowserFlowTests.Login_SetsTheAcceptedCookies_AndNoTokenReachesTheBrowser` (WebKit) |
| Failing calls | A navigation wait right after a form submission. Each is a step before the behaviour under test:<br>- the final `WaitForURLAsync` of `BrowserEnvironment.SignInAsync` ([BrowserEnvironment.cs](../../tests/Bff/EnterpriseCommerce.Bff.BrowserTests/BrowserEnvironment.cs)), after Identity's login form<br>- `page.WaitForLoadStateAsync()` after the change-password POST in `AfterAPasswordChange_TheBffReauthorizesSilently`<br>- `page.WaitForURLAsync(<BFF landing>)` after the login form in `Login_SetsTheAcceptedCookies_AndNoTokenReachesTheBrowser` |
| Symptom | `System.TimeoutException: Timeout 30000ms exceeded` from Playwright's `Frame.WaitForLoadStateAsync` (Playwright's default timeout; the tests set none). The hosts log no error: the next request in their log is the following test's. |
| Occurrences | **Windows**, local full-solution runs with every suite in parallel: 2026-11-21 (Phase 3 S3) and 2026-11-23 (S4 step 1), Chromium, the sign-in wait.<br>**Linux** (official Playwright 1.64.1 image), 2026-11-23, in short runs of one test class method (3 tests per run):<br>- 3 of 18 executions in the TS-2 experiment (Chromium sign-in ×1, WebKit change-password ×2)<br>- 1 of 9 with the original code (Chromium change-password)<br>- 1 of 9 in the cookie mutation runs (WebKit login, a test the mutation did not affect) |
| Not reproduced | The complete browser suite (18 tests):<br>- Windows: 18 of 18, three times on 2026-11-21 and twice on 2026-11-23 before the isolation change<br>- Linux image: none in the three final runs of 2026-11-23 (54 executions)<br>CI results up to 2026-11-23 are **not evidence** either way: the browser test step could not fail on a test failure ([CI-1](#ci-1-test-steps-could-not-fail-on-a-test-failure-fixed-2026-11-23)). |
| Assessment | Not only load-related, and not only Chromium. It appears under the parallel solution run and in short single-method runs on Linux, and it occurred with the original and the changed `AfterAPasswordChange` code alike. No run changed BFF or Identity code. The root cause is **not demonstrated**. Whether the per-test driver changes its frequency is not determined: no short runs were made before that change. |
| Policy until resolved | Report it; never treat it as a pass. Do not raise the timeout or add retries without evidence. |
| Next step | A dedicated investigation with a Playwright trace and host request timings captured on failure. Start with the short single-method Linux runs, where it occurs most often (about one execution in nine). Then fix the demonstrated cause. |

### TS-2: Linux WebKit crashes the Playwright driver on a cross-site redirect chain (worked around)

| | |
|---|---|
| Upstream | [microsoft/playwright#43067](https://github.com/microsoft/playwright/issues/43067), open, labelled `browser-webkit`. Linux only, since Playwright 1.63; blocked on a WebKit browser change (maintainer, 2026-10-28). The assertion is unchanged in v1.64.1, v1.65.1 and `main`. The fix PRs (#43068, #43233) were closed unmerged, and 1.65.x (WebKit 2388) adds a second trigger ([#44254](https://github.com/microsoft/playwright/issues/44254)). |
| Symptom | The driver throws `Error: Assertion error` at `_WKPage._onTargetCreated` (`assert(!this._provisionalPage)`) and exits. The test fails with `TargetClosedException: Process exited`. |
| Trigger | `AfterAPasswordChange_TheBffReauthorizesSilently` on Linux WebKit: the silent re-authorization (`/bff/login?silent=1`) is a cross-site redirect chain (BFF, then Identity's authorize endpoint, then the BFF callback) in a page that already visited both sites. CI run 45152730583 and every Linux run with the original code show it. |
| Workaround (in place) | The silent re-authorization runs in a **new page of the same browser context**: the same cookies, session and SSO state, with every assertion unchanged. Linux evidence, 2026-11-23, Playwright 1.64.1: <br>- original page: 3 crashes in 3 runs<br>- new page: no crash in 4 executions that reached the step (2 other executions timed out earlier, TS-1), then 18 of 18 in three complete suite runs<br>Chromium, Firefox and WebKit on Windows run the test as before. |
| Fallback | If the crash returns with the new page: an explicit skip of this test for WebKit on Linux only, naming TS-2 and #43067. Chromium, Firefox and Windows WebKit keep running it. |
| Removal criteria | When a Playwright release fixes #43067 (the issue closed with the fix shipped, and the assertion removed or replaced in `wkPage.ts`), upgrade in its own reviewed change. Then run the same-page variant three times in the official Linux image of that release. With no crash, return the silent step to the same page and close TS-2. |

### TS-3: a host that fails to start can surface `ObjectDisposedException` in `WebApplicationFactory` (worked around for the relay settings)

| | |
|---|---|
| Tests | `OutboxOperationsTests.InvalidRelaySettings_StopTheHostFromStarting`, removed on 2026-11-23. The same pattern is used by `SelfServiceOnboardingTests` for an invalid `MaxTenantsPerSubject`; no failure of it has been observed. |
| Symptom | The test starts a `WebApplicationFactory` host with invalid settings and expects the startup validation's message. Instead, `factory.CreateClient()` threw `System.ObjectDisposedException: Cannot access a disposed object`, without the validation message. |
| Occurrences | Windows, full-solution runs with every suite in parallel (browser tests on), 2026-11-23 (Phase 3 S4):<br>- first run: the two outbox cases<br>- second run, after the relay's constructor stopped reading its settings: the Kafka case<br>Every isolated run passed, and so did two runs of the TenantMerchant integration suite under load from two other suites. |
| Assessment | A race in the test host's teardown when the application fails during start, under heavy load. The validation itself is deterministic. The root cause inside `WebApplicationFactory` is **not demonstrated**. |
| Workaround (in place) | The relay settings are tested without a web host. `OutboxStartupValidationTests` builds the real Infrastructure registration and runs the `IStartupValidator` that the host runs before it starts. |
| Next step | If the onboarding test fails the same way, move it to the same approach. An upstream report needs a minimal reproduction. |

### TS-4: Catalog's chunked oversized body sometimes answered 400 instead of 413 (resolved 2026-11-24)

| | |
|---|---|
| Test | `Catalog.IntegrationTests.Api.RequestSizeLimitTests.AnOversizedBody_Is413` (Phase 4), on real Kestrel |
| Symptom | The chunked oversized request to `/brands` sometimes answered `400` with the problem "The request is malformed." instead of `413` (line 54). The declared-length request always answered `413`. |
| Occurrences | 2026-11-24, Windows: 1 failure in 15 TRX-recorded runs of the suite alone. In a probe of 300 chunked sequences inside the running suite, 18 answered `400`. Alone, 0 of 300 did. |
| Root cause | **The test's body was oversized *and* malformed:** 256 KiB + 1 zero bytes. A chunked body's size is known only once more than the limit has arrived. Kestrel then fails the body read with its `BadHttpRequestException` (413), and the requests that answered `413` logged it ("Reading the request body failed with an IOException ... Request body too large"). The minimal-API JSON binding parses whatever has arrived. When it parsed the leading zero bytes first, it refused the JSON as malformed: in Development, binding throws a `BadHttpRequestException` with status 400, which `GlobalExceptionHandler` answers as `400`. The `400` requests logged no size exception. Which happens first depends on timing, so the outcome varied under load. **Both are correct refusals before any authorization or store work.** The server is not at fault, and the 256 KiB contract holds. |
| Resolution | The 413 case now sends a body that is valid JSON up to the limit, so its size is the only reason to refuse it: 0 of 600 probe requests answered `400`. The expectation stays `413`. A new test, `AnOversizedMalformedChunkedBody_IsRefusedAsTooLargeOrMalformed_AndNeverProcessed`, pins the legitimate behaviour for a body that is both too large and malformed: `413` or `400`, a problem body, and no membership call. After the change, the suite passed 20 of 20 TRX-recorded runs. |
| Residual | Kestrel closes the connection after a `413` without reading the rest of an oversized upload. A client still sending may then see a reset instead of the response (`HttpRequestError.ResponseEnded`), as RFC 9110 allows. The probe saw this only on a client reused after a rejected upload, never in the test's own shape (a fresh connection for each oversized request, 600 of 600 `413`). |

### TS-5: Kafka metadata calls sometimes timed out on Windows (resolved 2026-11-24)

| | |
|---|---|
| Test | `Inventory.IntegrationTests.Kafka.KafkaPublicationTests.AMissingTopic_HaltsTheRelayWithoutSkipping_AndPublicationResumesOnceItExists` (Phase 5). Any Kafka client of the tests or the services could be affected. |
| Symptom | The test failed after about 14 s with `Confluent.Kafka.KafkaException: Local: Timed out` from `AdminClient.GetMetadata(topic, 10 s)` in `KafkaTestEnvironment.TopicExists`: the check that the relay never created the topic. The original failure's output was filtered away by the runner script. It was not reproduced in 15 TRX-recorded runs of the test class. A timed copy of the test reproduced it in 2 of 21 runs, both times at this step, and TRX recorded the exception. Several green runs had the same step take 2.3 s or 4.5 s. |
| Root cause | **An advertised address that resolves to an unpublished address family.** The Compose broker published its external port on `127.0.0.1` only, but advertised `localhost:9094`. After its bootstrap connection, every client connects to the advertised address. On Windows, `localhost` resolves to `::1` first, nothing listens there, and Windows refuses such a connect only after about 2 seconds, because it retries the attempt. librdkafka logged `Connect to ipv6#[::1]:9094 failed ... (after 2018ms in state CONNECT)`. A client that met this more than once, with its reconnect backoff, exceeded the 10-second metadata timeout. 40 new admin clients asking for the metadata of a missing topic: 18 took more than 2 s and 1 timed out (median 0.07 s, max 10.57 s). With IPv4 only: none (max 0.09 s). CI is not affected: Linux runners publish the port on both address families, and Linux refuses a closed port at once. |
| Resolution | The Compose broker advertises `EXTERNAL://127.0.0.1:9094`, the address its port is published on, and the development documentation uses `127.0.0.1:9094` as the bootstrap servers. With the default address family afterwards, 100 metadata calls answered in at most 0.17 s, with no timeout. No timeout was raised and no retry was added. The test is unchanged: its 10-second metadata deadline was never too short, the connection was. A broker created before the change keeps advertising `localhost` until `docker compose --profile kafka up -d --wait` recreates it. |

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

**First run after the fix** (45152730583, commit `a10e2e5`):
- The build-and-test job passed. Its results now count, including the mail-sink test.
- The browser job failed with 6 of 18 tests. The browser job had been unable to fail since it was added (`a3b3151`, 2026-11-20), so these failures were never verified before. They are:
  - the WebKit redirect `Set-Cookie` limitation (above)
  - the TS-2 driver crash
  - the failures of every later test that the crash caused through the shared driver
- All three are addressed in the test code (isolation, engine-aware raw-header assertions, the TS-2 workaround). The Playwright dependency and the CI configuration are unchanged.

## Planned

Code coverage reporting is planned. (MongoDB integration tests arrived with Catalog in Phase 4: `EC_TEST_MONGODB`.)
