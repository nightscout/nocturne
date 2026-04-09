# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Nocturne is a .NET 10 rewrite of the Nightscout diabetes management API with 1:1 API compatibility with the legacy JavaScript implementation. API versions v1, v2, and v3 maintain compatibility with the original Nightscout API; v4 is new.

## Development Commands

```bash
# Start the full stack (API + PostgreSQL + Web + services)
aspire run

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

# Frontend dev (standalone, without Aspire)
cd src/Web/packages/app && pnpm run dev

# Regenerate NSwag client + Zod schemas + remote functions
cd src/Web && pnpm run generate-api

# Regenerate just the NSwag TypeScript client
dotnet build -t:GenerateClient src/API/Nocturne.API/Nocturne.API.csproj

# EF Core migrations (must disable NSwag first)
dotnet build -p:GenerateNSwagClient=false
dotnet ef migrations add <Name> -p src/Infrastructure/Nocturne.Infrastructure.Data -s src/API/Nocturne.API
```

Aspire orchestrates everything: PostgreSQL, the API, the SvelteKit frontend, and background services. You only need to restart Aspire if `apphost.cs` changes. The NSwag client is regenerated automatically on Aspire startup.

## Architecture

Nocturne follows Clean Architecture. The solution has ~45 projects:

```
src/
├── API/Nocturne.API             # ASP.NET Core REST API (controllers for v1-v4 + admin)
├── Aspire/                      # .NET Aspire orchestration (AppHost, ServiceDefaults, SourceGenerators)
├── Connectors/                  # Data source integrations (Dexcom, Glooko, Libre, etc.)
├── Core/
│   ├── Nocturne.Core.Contracts  # Service interfaces
│   ├── Nocturne.Core.Models     # Domain models
│   ├── Nocturne.Core.Constants  # Shared constants
│   └── oref                     # OpenAPS reference algorithm (Rust)
├── Infrastructure/              # EF Core data access, caching, security
├── Services/                    # Background services (demo data, etc.)
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
3. **openapi-remote-codegen** generates SvelteKit server remote functions

Output lands in `src/Web/packages/app/src/lib/api/generated/`. The MetadataController exists solely to expose types to NSwag that aren't otherwise reachable through endpoints.

### Remote Functions Pattern

Frontend uses SvelteKit server functions (not raw fetch). Generated remote functions in `*.generated.remote.ts` provide type-safe server-side API calls with Zod validation:

```typescript
// query() for reads, command() for mutations
export const getById = query(z.string(), async (id) => {
  const { apiClient } = getRequestEvent().locals;
  return apiClient.entries.getById(id);
});
```

### Service Interface Pattern

Services are defined in `Core.Contracts` and registered as scoped:

```csharp
// Interface: src/Core/Nocturne.Core.Contracts/IEntryService.cs
// Implementation: src/API/Nocturne.API/Services/EntryService.cs
builder.Services.AddScoped<IEntryService, EntryService>();
```

### Nightscout Endpoint Compatibility

Use `[NightscoutEndpoint]` attribute to document legacy endpoint mapping:

```csharp
[HttpGet("current")]
[NightscoutEndpoint("/api/v1/entries/current")]
public async Task<ActionResult<Entry[]>> GetCurrentEntry(...)
```

### Connector Pattern

Data connectors implement `IConnectorService<TConfig>` with `AuthenticateAsync()` and `FetchGlucoseDataAsync()`. Configuration uses `IConnectorConfiguration` with a `Validate()` method. Reference implementation: `src/Connectors/Nocturne.Connectors.Dexcom/`.

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

```bash
# Full build (both containers, no push)
./scripts/build.sh

# Build with a specific tag
./scripts/build.sh v1.2.3

# Build and push to registry (requires docker login)
./scripts/build.sh latest --push

# Skip one container for faster iteration
SKIP_WEB=true ./scripts/build.sh
SKIP_API=true ./scripts/build.sh

# Test just the web Dockerfile independently
docker buildx build --file Dockerfile.web --load .
```

`scripts/build.sh` mirrors the CI pipeline locally: restores .NET, generates the API client (NSwag + Zod + remote codegen), verifies generated files, and builds both containers. Without `--push`, images are loaded into the local Docker daemon.

## Code Style Requirements

- **Backend is source of truth.** No calculations, categorization, or color computation on the frontend.
- **Always use remote functions**, never raw fetch/requests on the frontend.
- **No frontend-only models.** All TypeScript interfaces derive from the NSwag-generated client.
- **Strings/messages live on the frontend** (translation layer).
- **No emoji.** Use Lucide icons for UI elements.
- **No plans or design documents committed** to the repository.
