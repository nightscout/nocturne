# Nocturne tests

| Suite | Where | Runs with | CI job |
|---|---|---|---|
| .NET unit | `tests/Unit/*` | xUnit, SQLite in memory | `unit` |
| .NET integration, database | `tests/Integration/Nocturne.Infrastructure.Data.Tests`, `Nocturne.Connectors.Tests`, `Category=Integration` in `tests/Unit/Nocturne.API.Tests` | xUnit, one shared Postgres container | `integration-db` |
| .NET integration, API | `tests/Integration/Nocturne.API.Tests` | the API in-process on a real port, shared Postgres; Nightscout + MongoDB containers for parity and migration | `integration-api` |
| Alert engine parity | `tests/Parity`, `tests/Unit/Nocturne.Alerts.Native.Tests` | golden corpus, Rust FFI | `alert-corpus-check`, `alerts-ffi-parity` |
| Web unit | `src/Web/packages/{app,bot,bridge,cms,portal}` | vitest (node) | `web-app-tests`, `web-typecheck` |
| Web components | `src/Web/packages/app/**/*.svelte.test.ts` | vitest browser mode, headless chromium | `web-app-tests` |
| Web server render | `src/Web/packages/app/**/*.render.test.ts` | vitest | `web-typecheck` |
| End to end | `e2e/` | docker compose on the production images, vitest (API) + Playwright (web) | `e2e` |
| Migration upgrade | `e2e/scripts/migration-upgrade.ts` | the latest release, then this checkout, on one database | `migration-upgrade` |
| Benchmarks | `tests/Performance` | BenchmarkDotNet, manual | none |

No test starts Aspire. Docker is required for everything but the unit suites.

## Running

```bash
# .NET unit tests (name a project on Linux/macOS: the solution carries Windows-only projects)
dotnet test tests/Unit/Nocturne.API.Tests --filter "Category!=Integration"

# .NET integration tests
dotnet test tests/Integration/Nocturne.Infrastructure.Data.Tests
dotnet test tests/Integration/Nocturne.API.Tests
dotnet test tests/Unit/Nocturne.API.Tests --filter "Category=Integration"

# Web (from src/Web; the app suites need the generated API client, i.e. one API build)
pnpm --filter @nocturne/app exec vitest --config vitest.config.ts --run
pnpm --filter @nocturne/app exec vitest --config vitest.browser.config.ts --run
pnpm --filter @nocturne/bot run test

# End to end (from e2e/)
pnpm install
pnpm e2e            # build the images whose inputs changed, start, run everything, stop
pnpm e2e:api        # the same, API specs only
pnpm e2e:web        # the same, web specs only
pnpm e2e:up         # start and leave running: prints the tenant URL, a token, the DB URL
pnpm e2e:down
pnpm e2e:upgrade    # migration upgrade test
```

`dotnet test` accepts a directory only when it holds exactly one project or solution file, and a
bare `dotnet test` resolves `nocturne.sln`, which carries the Windows-only Desktop and Widget
projects; on Linux and macOS, name a project.

## End to end (`e2e/`)

`e2e/` is its own pnpm package with its own lockfile. It cannot join the `src/Web` workspace:
`Dockerfile.web` installs that workspace's lockfile with `--frozen-lockfile --offline` and a
build context holding only `src/Web/packages`, so a member outside it breaks the image build.

`docker-compose.yml` runs:

| Service | Image | Limits | Notes |
|---|---|---|---|
| `postgres` | `postgres:17.6-alpine` | 256 MB, 1 CPU | the production role bootstrap (`docs/postgres/container-init/00-init.sh`); tmpfs data directory, fsync, synchronous commit and full-page writes off |
| `mocks` | `node:24-alpine` + `e2e/mocks` | 96 MB | fake third-party vendors |
| `api` | `nocturne-api:e2e` | 512 MB, 2 CPUs | the SDK container build, as released; `NOCTURNE_ENABLE_DEV_ONLY_ENDPOINTS=true` |
| `web` | `nocturne-web:e2e` | 320 MB, 1 CPU | `Dockerfile.web`, as released |

Ports: API 1630, web 1631, Postgres 1633, mocks 1634 (`E2E_*_PORT` overrides them). Tenants are
reached as `http://<slug>.nocturne.localhost:1631`: `*.localhost` resolves to loopback in
browsers, the web server proxies `/api` to the API as it does behind the gateway, and specs that
call the API directly send the tenant host as `X-Forwarded-Host`. There is no gateway container.

`scripts/stack.ts build` tags each image with a hash of its inputs (the committed tree plus any
uncommitted change under `src/`, or under `src/Web` and the generated client for the web image)
and rebuilds only when that hash has no image. CI passes `E2E_BUILDX_CACHE=type=gha,...` for a
buildx layer cache. `E2E_SKIP_BUILD=1` uses whatever `:e2e` images exist; `E2E_KEEP_STACK=1`
leaves the stack running after `pnpm e2e`. On failure the compose logs are written to
`e2e/docker-compose-logs.txt`; Playwright keeps traces, screenshots and video for failed tests
only.

**The dev-only endpoints.** Specs seed tenants and mint sessions through
`/api/v4/dev-only/admin/seed-tenant` and `/api/v4/dev-only/auth/login`. They exist in
Development, or when `NOCTURNE_ENABLE_DEV_ONLY_ENDPOINTS` is exactly `true`
(`DevOnlyEndpoints`); the API logs a warning at startup when it is. `DevOnlyEndpointsTests`
asserts the default is off and that no appsettings file, compose bundle, Portainer stack or Helm
chart sets it.

**Specs.** `src/helpers/tenant.ts` seeds an isolated tenant with a random slug, so spec files and
Playwright tests run in parallel; it returns an owner-authenticated client, an anonymous one, and
the browser login link. `src/helpers/http.ts` is a thin typed fetch client (the generated NSwag
client lives inside the SvelteKit app and is gitignored). `src/helpers/data.ts` builds CGM series
and treatments.

**Fake vendors.** `e2e/mocks/server.ts` serves each vendor under a path prefix; a connector is
pointed at `http://mocks:8080/<vendor>`. Every vendor also answers `GET /<vendor>/__requests`
with the requests it served (and `DELETE` to clear them), for asserting what a connector called.
Nightscout (`vendors/nightscout.ts`) serves 48 hours of generated five-minute readings and a few
treatments anchored to the request time, and honours the connector's `count` and date queries.
To add a vendor, write a module exporting a `Vendor` and list it in `server.ts`.

**Migration upgrade.** `pnpm e2e:upgrade` starts the latest release
(`ghcr.io/nightscout/nocturne/nocturne-api:latest`; override with `E2E_PREVIOUS_API_IMAGE`) on a
fresh database, seeds three tenants (sample data, uploads, lab results, share links, a connector
with a secret, an API token), swaps the API container for the image built from the checkout on
the same database, and requires it to become healthy and read every tenant back unchanged, both
through the old API token and through a new session. Features the previous release lacks are
skipped there and left out of the comparison. It runs on its own ports beside `pnpm e2e:up`.

## Integration test infrastructure

**`SharedPostgres`** (`tests/Shared/Nocturne.Tests.Shared/Infrastructure`) starts one
`postgres:17.6` container per test process with the production role bootstrap, durability off on
tmpfs. It migrates a template database once (with the share-RLS and storage-parameter
reconcilers the API runs at startup) and hands each fixture a `CREATE DATABASE … TEMPLATE` clone,
reachable as `nocturne_migrator`, `nocturne_app` or the bootstrap superuser.
`CreateEmptyDatabaseAsync` clones the bootstrapped, unmigrated database for tests that walk the
migration chain themselves.

**`ApiIntegrationTestFixture`** runs the API with `WebApplicationFactory` on a real Kestrel port
(so HttpClient and SignalR connect over sockets) as Development, against its own `SharedPostgres`
database, with `BASE_DOMAIN=localhost:{port}`. Use `ApiFactory` for any `WebApplicationFactory`
over the API: the API's entry point also carries NSwag's static `CreateHostBuilder`, which
`WebApplicationFactory` otherwise prefers, and that host has no endpoints.

**SQLite unit databases.** `TestDbContextFactory.CreateSqlite*` copies a schema built once per
process into each test's in-memory database with SQLite's backup API instead of running
`EnsureCreated` per test.

## Coverage

CI collects Cobertura coverage from the .NET jobs (`--settings tests/coverage.runsettings
--collect "XPlat Code Coverage"`, which excludes test assemblies, migrations and generated code)
and from the web vitest suites (`--coverage`, the v8 provider). The `coverage` job merges the
.NET reports with ReportGenerator and runs `.github/scripts/coverage-report.mjs`, which writes
line and branch coverage per area (API, Infrastructure, Core, Connectors, Services, app, bot,
portal, bridge, cms), the change against the last baseline main uploaded, and the coverage of the
lines the pull request adds. The report goes to the job summary and, through
`coverage-comment.yml`, to one comment on the pull request that is updated on every push. That
workflow runs on `workflow_run` with a write token, so it works for pull requests from forks; it
never checks out the pull request and only comments where the pull request's head is the tested
commit.

The gate: the job fails when patch coverage is below `COVERAGE_PATCH_THRESHOLD` (a repository
variable, default 60%). Nothing else about coverage fails a build.

Locally:

```bash
dotnet test tests/Unit/Nocturne.API.Tests --filter "Category!=Integration" \
  --settings tests/coverage.runsettings --collect "XPlat Code Coverage" --results-directory TestResults/unit
dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator "-reports:TestResults/**/coverage.cobertura.xml" -targetdir:coverage-merged -reporttypes:Cobertura
(cd src/Web && pnpm --filter @nocturne/app exec vitest --config vitest.config.ts --run --coverage)
node .github/scripts/coverage-report.mjs --base origin/main \
  --reports 'coverage-merged/Cobertura.xml,src/Web/packages/*/coverage/**/cobertura-coverage.xml'
```

## Parallelism

- .NET: `dotnet test` over a solution builds and runs the test assemblies in parallel (MSBuild
  nodes); xUnit runs collections in parallel on one thread per core, which measured faster than a
  lower cap for the same memory. Integration fixtures each get their own cloned database, so their
  collections run in parallel too.
- vitest: the app unit suite uses half the cores, between two and four; the browser suite two pages,
  always headless; the other packages run their default pool.
- e2e: vitest and Playwright use half the cores, at least two (`E2E_API_WORKERS`,
  `E2E_WEB_WORKERS`); every test seeds its own tenant.

## CI

`.github/workflows/tests.yml` starts with a `changes` job (`dorny/paths-filter`) that skips jobs a
pull request does not touch; pushes to main and manual runs test everything. Require
`tests-passed`: it is green when every job it depends on passed or was skipped.

Container images are published by `docker-publish.yml`: `:develop` and `:main-<sha7>` on every
push to main, the version tag and `:latest` on a `v*` release tag (a pre-release tag does not move
`:latest`). Pull requests publish no images; the `e2e` and `migration-upgrade` jobs build theirs
on the runner.

## Adding tests

- Unit tests mirror the source tree: `tests/Unit/Nocturne.{Project}.Tests/`.
- `[Trait("Category", "Integration")]` marks a test that needs Docker; `TestCategoryTraitTests`
  requires it to bind to a fixture.
- Nightscout import (`src/API/Nocturne.API/Services/Migration`) is covered by
  `tests/Unit/Nocturne.API.Tests/Migration/` and `tests/Integration/Nocturne.API.Tests/Migration/`.
- Benchmarks: `dotnet run --project tests/Performance/Nocturne.API.Performance.Tests -c Release -- --list flat`.
