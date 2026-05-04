# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Nocturne is a .NET 10 rewrite of the Nightscout diabetes management API with 1:1 API compatibility with the legacy JavaScript implementation. API versions v1, v2, and v3 maintain compatibility with the original Nightscout API; v4 is new.

## Development Commands

```bash
# Start the full stack (API + PostgreSQL + Web + services)
aspire start

# Build solution
dotnet build

# Run unit tests (excludes integration/performance)
dotnet test --filter "Category!=Integration&Category!=Performance"

# Run a single test class
dotnet test --filter "FullyQualifiedName~EntryServiceTests"

# Run integration tests (requires Docker)
cd tests/Infrastructure/Docker && docker-compose -f docker-compose.test.yml up -d
dotnet test --filter "Category=Integration"

# Frontend type checking
cd src/Web/packages/app && pnpm run check

# Regenerate just the NSwag TypeScript client
dotnet build -t:GenerateClient src/API/Nocturne.API/Nocturne.API.csproj

# EF Core migrations (must disable NSwag first)
dotnet build -p:GenerateNSwagClient=false
dotnet ef migrations add <Name> -p src/Infrastructure/Nocturne.Infrastructure.Data -s src/API/Nocturne.API
```

Aspire orchestrates everything: PostgreSQL, the API, the SvelteKit frontend, and background services. A YARP gateway is the single external HTTPS endpoint; API and Web run as plain HTTP behind it. You only need to restart Aspire if its `Program.cs` changes. The NSwag client is regenerated automatically on Aspire startup. If you come across a roadblock from the `.dll`s being in use, just kill the dotnet processes.

### Worktrees

Git worktrees are supported. In the main checkout, `aspire start` uses persistent Postgres (named volume, pgAdmin) and binds the gateway to `https://localhost:1612`. In a worktree, Postgres is automatically ephemeral (anonymous volume, no pgAdmin) and ports are dynamic.

**Always use `--isolated` when running Aspire from a worktree** to avoid dashboard port collisions with the main instance:

```bash
aspire run --isolated
```

`--isolated` randomizes all Aspire infrastructure ports (dashboard, OTLP, resource service) and creates isolated user secrets. Without it, the worktree shares `launchSettings.json` ports with main and will fail to start if main is already running.

To force persistent mode in a worktree (e.g. long-lived debugging): `NOCTURNE_DB_PERSISTENCE=persistent aspire run --isolated`.

## Architecture

Nocturne follows Clean Architecture.

```
src/
├── API/Nocturne.API             # ASP.NET Core REST API (controllers for v1-v4 + admin)
├── Aspire/                      # .NET Aspire orchestration (AppHost, ServiceDefaults, SourceGenerators)
├── Connectors/                  # Data source integrations (Dexcom, Glooko, Libre, etc.)
├── Core/
│   ├── Nocturne.Core.Contracts  # Service interfaces
│   ├── Nocturne.Core.Models     # Domain models
│   └── Nocturne.Core.Constants  # Shared constants
├── Infrastructure/              # EF Core data access, caching, security
├── Services/                    # Background services (demo data, etc.)
├── Portal/                      # Marketing website
└── Web/                         # pnpm monorepo
    └── packages/
        ├── app/                 # @nocturne/app - SvelteKit frontend
        ├── bot/                 # @nocturne/bot - bot framework for Discord et al.
        ├── portal/              # @nocturne/portal - SvelteKit portal frontend
        └── bridge/              # @nocturne/bridge - SignalR to Socket.IO bridge
```

### API Client Generation Pipeline

Three-stage pipeline runs as MSBuild post-build targets on the API project:

1. **NSwag** generates OpenAPI spec → TypeScript client interfaces (`nswag.json`)
2. **Zod schema generator** creates validators from the OpenAPI spec
3. **openapi-remote-codegen** generates SvelteKit server remote functions from controller endpoints marked with [RemoteQuery], [RemoteCommand], or [RemoteFormData] attributes

Output lands in `src/Web/packages/app/src/lib/api/generated/`. The MetadataController exists solely to expose types to NSwag that aren't otherwise reachable through endpoints.

### Timestamp Handling

Domain models use **mills-first** timestamps. `Entry.Mills` (Unix milliseconds) is the source of truth; `Entry.Date` and `Entry.DateString` are computed properties.

## Database

- **PostgreSQL** via Entity Framework Core with 70+ migrations
- Domain models → Database entities via mappers in `Infrastructure.Data/Mappers/`
- Tables use snake_case (`entries`, `treatments`)
- UUID v7 for new records; `OriginalId` preserved for MongoDB migration compatibility
- Row Level Security for multitenancy

### Row Level Security

Tenant-scoped tables enforce isolation via PostgreSQL Row Level Security.
Two roles are used:

- `nocturne_migrator` — owns the schema, runs migrations. NOSUPERUSER NOBYPASSRLS.
- `nocturne_app` — runtime DbContext pool. Owns nothing. NOSUPERUSER NOBYPASSRLS.
- `nocturne_web` — SvelteKit web app's bot-framework state (`chat_state_*`
  tables created on first run by `@chat-adapter/state-pg`). Owns only those
  tables. NOSUPERUSER NOBYPASSRLS. The `chat_state_*` tables are
  intentionally NOT tenant-scoped and NOT covered by RLS — they're keyed by
  chat-platform IDs (Discord user ID, Telegram chat ID, etc.) and hold no
  PHI. PHI is only ever fetched over HTTP through the Nocturne API, which
  enforces RLS server-side. NOBYPASSRLS on the role is defense in depth.

FORCE ROW LEVEL SECURITY is enabled on every tenant-scoped table, so even
the migrator obeys policies. **Data migrations cannot SELECT or UPDATE
tenant-scoped tables without first setting the tenant context**:

    SELECT set_config('app.current_tenant_id', '<uuid>', false);
    -- then query/update

Schema-only migrations (CREATE/ALTER TABLE, CREATE INDEX, etc.) are
unaffected. If a data migration needs to touch multiple tenants, loop over
tenants and set the GUC per iteration.

Roles are created by `docs/postgres/container-init/00-init.sh` (container
init, bind-mounted into the Postgres container) or
`docs/postgres/bootstrap-roles.sql` (bring-your-own PostgreSQL, run once
manually as superuser). The BYO script is intentionally NOT in the
container-init directory — it refuses to run with placeholder passwords
and would abort container startup if Postgres picked it up. Never GRANT
BYPASSRLS to either role.

## Testing

- **xUnit** + **FluentAssertions** + **Moq**
- Tests mirror source structure: `tests/Unit/Nocturne.{Project}.Tests/`
- `[Trait("Category", "Integration")]` for integration tests
- Integration tests use `WebApplicationFactory<Program>` and Testcontainers

## Web Frontend

- **SvelteKit 2** / **Svelte 5** (runes), **Tailwind CSS 4**, **shadcn-svelte**, **layerchart**, **Zod 4**
- **pnpm** workspaces (Node.js 24+, pnpm 9+)

## Local Container Build

`scripts/build.cs` (run via `dotnet run scripts/build.cs`) mirrors the CI pipeline locally: restores .NET, generates the API client (NSwag + Zod + remote codegen), verifies generated files, and builds both containers. Without `--push`, images are loaded into the local Docker daemon. `scripts/publish-release.cs` generates the production Docker Compose bundle (compose + `.env.example` + init script) that gets attached to GitHub Releases.

## Code Style Requirements

- **Backend is source of truth.** No calculations, categorization, or color computation on the frontend.
- **No frontend-only models.** All TypeScript interfaces derive from the NSwag-generated client.
- **Always use remote functions**, never raw fetch/requests on the frontend. Use the remote functions attribute to automatically generate type-safe API calls with Zod validation.
- **Strings/messages live on the frontend** (translation layer).
- **No emoji.** Use Lucide icons for UI elements.
