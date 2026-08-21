## Project Overview

Nocturne is a .NET 10 rewrite of the Nightscout diabetes management API with 1:1 API compatibility with the legacy JavaScript implementation. v1, v2, and v3 are all earmarked for keeping with the original API.

## Running the application

To run the application run the following command:

```
aspire run
```

If there is already an instance of the application running it will prompt to stop the existing instance. You only need to restart the application if code in `apphost.cs` is changed, but if you experience problems it can be useful to reset everything to the starting state.

## Development Commands

```bash
# Start with Aspire (orchestrates all services + PostgreSQL)
aspire run

# Build solution
dotnet build

# Run unit tests (excludes integration/performance/E2E)
dotnet test --filter "Category!=Integration&Category!=Performance&Category!=E2E"

# Run integration tests (requires Docker containers)
cd tests/Infrastructure/Docker && docker-compose -f docker-compose.test.yml up -d
dotnet test --filter "Category=Integration"

# Run the end-to-end suite (opt-in; stands up the whole Aspire stack)
dotnet test tests/E2E/Nocturne.E2E.Tests -p:RunE2E=true

# Type checking for frontend
cd src/Web/packages/app && pnpm run check
```

Aspire creates the NSwag client on startup, and orchestrates everything. All you need to do to regenerate the NSwag client is `aspire start`.

## Architecture

Nocturne follows Clean Architecture. Frontend interfaces are also derived from the NSwag model (although due to NSwag peculiarities, models that need to be generated have to exist in an endpoint, which is what the metadata controller is for)

```
src/
├── API/Nocturne.API           # REST API (Nightscout-compatible endpoints)
├── Aspire/                    # .NET Aspire service orchestration
├── Connectors/                # Data source integrations (Dexcom, Glooko, Libre, etc.)
├── Core/
│   ├── Nocturne.Core.Contracts    # Service interfaces
│   ├── Nocturne.Core.Models       # Domain models
│   ├── Nocturne.Core.Constants    # Shared constants (ServiceNames)
│   └── oref                       # Oref rust library
├── Infrastructure/            # Data access, caching, security
├── Services/                  # Background services
└── Web/                       # pnpm monorepo (SvelteKit app + WebSocket bridge)
    └── packages/
        ├── app/               # @nocturne/app - SvelteKit frontend
        ├── portal/            # @nocturne/portal - SvelteKit frontend for the portal
        └── bridge/            # @nocturne/bridge - SignalR to Socket.IO bridge

tests/
├── Unit/                      # Unit tests
├── Integration/               # Integration tests (use Testcontainers)
├── E2E/                       # Aspire-hosted end-to-end tests (opt-in, see Testing)
└── Performance/               # Performance benchmarks
```

## Key Patterns

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

Data connectors derive from `BaseConnectorService<TConfig>`, which implements `IConnectorService<TConfig>`:

- Implement `AuthenticateAsync()` and `PerformSyncInternalAsync()`
- Configuration via `IConnectorConfiguration` with `Validate()` method
- Reference: `src/Connectors/Nocturne.Connectors.Dexcom/`

### Timestamp Handling

Domain models use **mills-first** timestamps - Unix milliseconds is canonical:

```csharp
// Entry.Mills is the source of truth
// Entry.Date and Entry.DateString are computed properties
```

## Database

- **PostgreSQL** via Entity Framework Core
- Domain models (`Entry`) → Database entities (`EntryEntity`) via mappers in `Infrastructure.Data/Mappers/`
- Tables use snake_case: `entries`, `treatments`
- UUID v7 for new records, preserve `OriginalId` for MongoDB migration compatibility

## Testing

- **xUnit** + **FluentAssertions** + **Moq**
- Tests mirror source structure: `tests/Unit/Nocturne.{Project}.Tests/`
- Use `[Trait("Category", "Integration")]` for integration tests
- Integration tests use `WebApplicationFactory<Program>` and Testcontainers

### End-to-end tests

`tests/E2E/Nocturne.E2E.Tests` boots the whole Aspire stack from `AppHostFixture`, so it is
excluded from test collection by default (`IsTestProject` is `$(RunE2E)`, which defaults to
`false`) — a mistyped `--filter` cannot drag the stack into a unit run. It still compiles as
part of `dotnet build nocturne.sln`. Opt in explicitly:

```bash
dotnet test tests/E2E/Nocturne.E2E.Tests -p:RunE2E=true
```

No workflow runs it: Aspire.Hosting.Testing's DCP orchestration never completes on
GitHub-hosted runners (see the trailing note in `.github/workflows/tests.yml`). A workflow
that revives it needs `-p:RunE2E=true` on the `dotnet test` invocation.

## Web Frontend

- **SvelteKit 2** with **Svelte 5** (runes-based reactivity)
- **Tailwind CSS 4** for styling
- **shadcn-svelte** component patterns, including the variables
- **layerchart** for data visualization
- **Zod 4** for schema validation
- Remote functions which wrap around the NSwag client for full type safety
- Uses **pnpm** workspaces (requires Node.js 24+, pnpm 9+)

## Important Code Style Requirements:

- Messages and strings are always on the frontend- that's where our translation layer will live.
- We always use remote functions, never raw requests.
- We use the backend as the source of truth. Abstain from creating frontend models or interfaces that are not derived from the NSwag client.
- We never perform calculations on the frontend.
- We never commit plans or design documents to the repository- these are ephemeral and just create noise in the git history.
- We never use emoji generally, and we prefer Lucide icons over unicode emoji for UI elements.

This repository is set up to use Aspire. Aspire is an orchestrator for the entire application and will take care of configuring dependencies, building, and running the application. The resources that make up the application are defined in `apphost.cs` including application code and external dependencies.

## Comments

A comment earns its place by carrying something a reader could not work out from the code, the
names and the types in front of them. We do not narrate. Restating a signature or a body is not a
weaker comment, it is one to delete.

Delete on sight:

- **Narration** — `// Get the user's timezone` above a call to `GetTimezone()`, or
  `/// <summary>Gets or sets the policy.</summary>` on a property named `Policy`. Keep a `<param>`
  only for a non-obvious default or caller contract.
- **Benefit tails** — "..., so report pages no longer need to paginate every raw reading out."
- **Change history** — "previously this was X", "we now do Y". That belongs in the commit message;
  someone reading the file does not need the diff's story.
- **Step-by-step banners** — `// 1. Load`, `// 2. Filter` over code whose structure already says so.
- **Essays** — a paragraph where a sentence would do. A long comment is right when an invariant is
  genuinely load-bearing, and wrong when it re-derives reasoning.

Rationale lives at exactly one site — the type or method that exists to solve the problem — and
everywhere else refers to it with `<see cref="..."/>`. Repeating an argument across call sites is a
DRY violation in prose, and every copy can drift out of step with the others.

State the non-obvious why, then stop.
