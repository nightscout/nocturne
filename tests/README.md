# Nocturne Testing Framework

This directory contains the test suites for the Nocturne project.

## Overview

- **Unit Tests**: Fast, isolated component testing with mocking
- **Integration Tests**: Real databases and a running API, via Testcontainers and Aspire
- **Parity Tests**: Golden corpora that pin behaviour — legacy Nightscout responses and the alert engine
- **Performance Tests**: BenchmarkDotNet benchmarks
- **E2E Tests**: The whole Aspire stack, opt-in (see below)
- **Shared fixtures**: Helpers, fakes and seed data reused across suites

## Directory Structure

```
tests/
├── Unit/                          # Fast unit tests with mocking
├── Integration/                   # Integration tests with real databases
├── E2E/                           # Aspire-hosted end-to-end tests (opt-in)
├── Parity/                        # Alert-engine golden corpus and its generator
├── Performance/                   # BenchmarkDotNet benchmark projects
├── Shared/                        # Fixtures and helpers shared across suites
└── Infrastructure/                # Test environment setup
    └── Docker/                    # Docker containers for testing
        ├── docker-compose.test.yml
        └── postgresql-init/       # PostgreSQL initialization
```

## Quick Start

`dotnet test` accepts a directory only when it holds exactly one project or solution file, so a project directory works and `tests/Unit/` does not (MSB1003). Given no path — as in every bare `dotnet test` in this file — it resolves `nocturne.sln` from the repo root, and the solution carries the Windows-only Desktop and Widget projects; on Linux, name a project.

```bash
# Everything collected, E2E excluded (solution-scoped, so Windows only)
dotnet test

# One category (also solution-scoped)
dotnet test --filter "Category=Integration"

# One project, on any OS
dotnet test tests/Unit/Nocturne.API.Tests --filter "Category!=Integration&Category!=E2E"

# Run the end-to-end suite (stands up the whole Aspire stack)
dotnet test tests/E2E/Nocturne.E2E.Tests -p:RunE2E=true
```

## Docker Setup for Integration Tests

Integration tests need Docker: Testcontainers starts PostgreSQL for the data suites, and MongoDB plus a mock Nightscout server for the migration suite. Unit tests have no external dependencies.

### Installing Docker

**Windows**

1. Download and install [Docker Desktop for Windows](https://docs.docker.com/desktop/windows/install/)
2. Start Docker Desktop
3. Ensure WSL 2 backend is enabled (recommended)

**macOS**

1. Download and install [Docker Desktop for Mac](https://docs.docker.com/desktop/mac/install/)
2. Start Docker Desktop

**Linux**

1. Install Docker Engine using your distribution's package manager
2. Start the Docker service
3. Add your user to the docker group (optional, to run without sudo)

### Verifying Docker Installation

```bash
docker --version
docker run hello-world
```

## Performance Testing

`Nocturne.API.Performance.Tests` benchmarks entry projection, treatment mapping and the statistics service. `Nocturne.Infrastructure.Data.Performance.Tests` benchmarks pagination, insulin-delivery and sensor-glucose queries and the linked-records filter against a Testcontainers PostgreSQL.

Both are BenchmarkDotNet hosts: list what they carry, then filter down to what you want (`--filter '*'` runs everything).

```bash
dotnet run --project tests/Performance/Nocturne.API.Performance.Tests -c Release -- --list flat
dotnet run --project tests/Performance/Nocturne.API.Performance.Tests -c Release -- --filter '*EntityToDomain_100'
```

## Fast Tests (Excludes slow tests by default)

```bash
# Run all unit tests (excludes Integration, Performance and E2E tests)
dotnet test --filter "Category!=Integration&Category!=Performance&Category!=E2E"

# Run unit tests with coverage
dotnet test --filter "Category!=Integration&Category!=Performance&Category!=E2E" --collect:"XPlat Code Coverage"
```

## Slow Tests (Run only when needed)

### Performance Tests

`Category=Performance` marks the BenchmarkDotNet runners under `tests/Performance` and nothing else;
the whole-solution filters above exist to skip them. Nothing under `tests/Unit` carries the trait, so
a per-project unit run needs no `Category!=Performance` (`TestCategoryTraitTests` enforces both).

```bash
# Run only the benchmark runners
dotnet test --filter "Category=Performance"
```

### Integration Tests

```bash
# Run all integration tests (Docker required)
dotnet test --filter "Category=Integration"
```

## Complete Test Suite (All tests including slow ones)

```bash
# Run everything that is collected (will take a long time; E2E still needs -p:RunE2E=true)
dotnet test

# Run everything with coverage
dotnet test --collect:"XPlat Code Coverage"
```

## CI/CD Recommendations

### Pull Request Validation (Fast)

```bash
dotnet test --filter "Category!=Integration&Category!=Performance&Category!=E2E" --logger "trx" --results-directory TestResults
```

### Nightly/Weekly Full Test Suite

```bash
dotnet test --logger "trx" --results-directory TestResults --collect:"XPlat Code Coverage"
```

### Release Validation

```bash
# Run performance benchmarks
dotnet test --filter "Category=Performance" --logger "trx" --results-directory TestResults

# Run integration tests
dotnet test --filter "Category=Integration" --logger "trx" --results-directory TestResults
```

## Adding New Tests

### Migration tests

Nightscout import is an API feature (`src/API/Nocturne.API/Services/Migration`), so its coverage sits with the API suites: unit tests in `tests/Unit/Nocturne.API.Tests/Migration/`, and `tests/Integration/Nocturne.API.Tests/Migration/` driving both source paths — a MongoDB Testcontainer seeded from Nightscout fixtures, and a mock Nightscout API server.

### API Tests

1. **Unit Tests**: Create in appropriate folder (Controllers, Services, Models)
2. **Integration Tests**: Add to Integration folder with database setup
3. **Use mocking**: For external dependencies in unit tests
4. **Follow patterns**: Use existing base classes and patterns
