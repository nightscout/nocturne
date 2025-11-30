# Nocturne - AI Coding Agent Instructions

## Project Overview

Nocturne is a modern .NET 10 rewrite of the Nightscout diabetes management API with full feature parity. The goal is 1:1 API compatibility with the legacy JavaScript Nightscout implementation.

## Architecture

```
src/
├── API/Nocturne.API          # REST API (Nightscout-compatible endpoints)
├── Aspire/                   # .NET Aspire service orchestration
├── Connectors/               # Data source integrations (Dexcom, Glooko, Libre, etc.)
├── Core/
│   ├── Nocturne.Core.Contracts   # Service interfaces (IEntryService, etc.)
│   ├── Nocturne.Core.Models      # Domain models (Entry, Treatment, etc.)
│   └── Nocturne.Core.Constants   # Shared constants (ServiceNames)
├── Infrastructure/           # Data access, caching, security
└── Services/                 # Background services, notifications
```

## Key Patterns

### Service Interface Pattern

All services are defined in `Core.Contracts` and registered as scoped:

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

New data connectors extend `IConnectorService<TConfig>`:

- Implement `AuthenticateAsync()` and `FetchGlucoseDataAsync()`
- Configuration via `IConnectorConfiguration` with `Validate()` method
- See `src/Connectors/Nocturne.Connectors.Dexcom/` for example

### Timestamp Handling (Critical)

Domain models use **mills-first** timestamps - Unix milliseconds is canonical:

```csharp
// Entry.Mills is the source of truth
// Entry.Date and Entry.DateString are computed properties
```

## Development Commands

```bash
# Start with Aspire (orchestrates all services + PostgreSQL)
dotnet run --project src/Aspire/Nocturne.Aspire.Host

# Run just the API
dotnet run --project src/API/Nocturne.API

# Run tests (exclude integration/performance)
dotnet test --filter "Category!=Integration&Category!=Performance"

# Run integration tests (requires Docker)
cd tests/Infrastructure/Docker && docker-compose -f docker-compose.test.yml up -d
dotnet test --filter "Category=Integration"

# Generate TypeScript API client
cd src/API/Nocturne.API && nswag run nswag.json
```

**VS Code Tasks**: Use `start-aspire`, `start-api`, `build` from Command Palette.

## Testing Conventions

- **xUnit** + **FluentAssertions** + **Moq**
- Tests mirror source structure: `tests/Unit/Nocturne.{Project}.Tests/`
- Use `[Trait("Category", "Integration")]` for integration tests
- Integration tests use `WebApplicationFactory<Program>` and Testcontainers

## Database & Entities

- **PostgreSQL** via Entity Framework Core
- Domain models (`Entry`) → Database entities (`EntryEntity`) via mappers in `Infrastructure.Data/Mappers/`
- Tables use snake_case: `entries`, `treatments`
- UUID v7 for new records, preserve `OriginalId` for MongoDB migration compatibility

## Constants & Service Names

Centralized in `src/Core/Nocturne.Core.Constants/ServiceNames.cs`:

```csharp
ServiceNames.NocturneApi      // "nocturne-api"
ServiceNames.PostgreSql       // "nocturne-postgres"
ServiceNames.DexcomConnector  // "dexcom-connector"
```

## Web Frontend

Located in `src/Web/` - pnpm monorepo with SvelteKit:

```bash
cd src/Web && pnpm dev
```

## Key Files to Reference

| Concept           | Example File                                               |
| ----------------- | ---------------------------------------------------------- |
| API Controller    | `src/API/Nocturne.API/Controllers/V1/EntriesController.cs` |
| Service Interface | `src/Core/Nocturne.Core.Contracts/IEntryService.cs`        |
| Domain Model      | `src/Core/Nocturne.Core.Models/Entry.cs`                   |
| Connector         | `src/Connectors/Nocturne.Connectors.Dexcom/`               |
| Aspire Host       | `src/Aspire/Nocturne.Aspire.Host/Program.cs`               |
| Unit Test         | `tests/Unit/Nocturne.API.Tests/`                           |
