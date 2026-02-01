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

# Run unit tests (excludes integration/performance)
dotnet test --filter "Category!=Integration&Category!=Performance"

# Run integration tests (requires Docker containers)
cd tests/Infrastructure/Docker && docker-compose -f docker-compose.test.yml up -d
dotnet test --filter "Category=Integration"

# Type checking for frontend
cd src/Web/packages/app && pnpm run check
```

Aspire creates the NSwag client on startup, and orchestrates everything. All you need to do to regenerate the NSwag client is `aspire run`.

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
│   ├── Nocturne.Core.Oref         # Oref P:bindings
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

Data connectors extend `IConnectorService<TConfig>`:

- Implement `AuthenticateAsync()` and `FetchGlucoseDataAsync()`
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

## General recommendations for working with Aspire

1. Before making any changes always run the apphost using `aspire run` and inspect the state of resources to make sure you are building from a known state.
2. Changes to the _apphost.cs_ file will require a restart of the application to take effect.
3. Make changes incrementally and run the aspire application using the `aspire run` command to validate changes.
4. Use the Aspire MCP tools to check the status of resources and debug issues.

## Checking resources

To check the status of resources defined in the app model use the _list resources_ tool. This will show you the current state of each resource and if there are any issues. If a resource is not running as expected you can use the _execute resource command_ tool to restart it or perform other actions.

## Debugging issues

IMPORTANT! Aspire is designed to capture rich logs and telemetry for all resources defined in the app model. Use the following diagnostic tools when debugging issues with the application before making changes to make sure you are focusing on the right things.

1. _list structured logs_; use this tool to get details about structured logs.
2. _list console logs_; use this tool to get details about console logs.
3. _list traces_; use this tool to get details about traces.
4. _list trace structured logs_; use this tool to get logs related to a trace

## Official documentation

IMPORTANT! Always prefer official documentation when available. The following sites contain the official documentation for Aspire and related components

1. https://aspire.dev
2. https://learn.microsoft.com/dotnet/aspire
3. https://nuget.org (for specific integration package details)
