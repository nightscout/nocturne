# Nocturne

A modern, high-performance diabetes management platform built with .NET 9. Nocturne is a complete rewrite of the Nightscout API with full feature parity, providing native C# implementations of all endpoints with optimized performance and modern cloud-native architecture.

## What is Nocturne?

Nocturne is a comprehensive diabetes data platform that provides:

- **Complete Nightscout API Implementation** - All Nightscout endpoints natively implemented in C# with full compatibility
- **Data Connectors** - Native integration with major diabetes platforms (Dexcom, Glooko, LibreLinkUp, MiniMed CareLink, MyFitnessPal, Nightscout)
- **Real-time Updates** - WebSocket/SignalR support for live glucose readings and alerts
- **Advanced Analytics** - Comprehensive glucose statistics, time-in-range calculations, and reports
- **Cloud-Native** - Built on .NET Aspire for seamless local development and cloud deployment

## Architecture

```
Nocturne/
├── src/
│   ├── API/                        # REST API (Nightscout-compatible)
│   ├── Connectors/                # Data source integrations
│   ├── Core/                       # Domain models and interfaces
│   ├── Infrastructure/             # Data access and caching
│   ├── Aspire/                     # .NET Aspire orchestration
│   └── Tools/                      # CLI tools
└── tests/                          # Comprehensive test suite
```

## Key Features

- **Full Nightscout API Parity** - All v1, v2, and v3 endpoints
- **High Performance** - Optimized queries with PostgreSQL and Redis caching
- **Authentication** - JWT-based auth with API_SECRET support
- **Real-time** - SignalR hubs for live data streaming
- **Data Connectors** - Dexcom Share, Glooko, LibreLinkUp, MiniMed CareLink, MyFitnessPal, Nightscout
- **PostgreSQL** - Modern relational database with EF Core migrations
- **Redis Cache** - Distributed caching for optimal performance
- **Observability** - OpenTelemetry integration for monitoring
- **Containerized** - Docker support for all services

## Quick Start with .NET Aspire

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or later
- [Docker Desktop](https://www.docker.com/products/docker-desktop) (for local PostgreSQL)

### Run with Aspire

.NET Aspire orchestrates all services with a single command:

```bash
dotnet aspire run
```

Aspire will automatically:

- Start PostgreSQL in a container
- Run database migrations
- Start the Nocturne API
- Launch any configured data connectors
- Set up service discovery and health checks
- Open the Aspire dashboard at `http://localhost:17257`

### Access the API

Once Aspire starts:

- **API**: http://localhost:1612
- **API Documentation**: http://localhost:1612/scalar
- **Aspire Dashboard**: http://localhost:15888

## Configuration

### appsettings.json

The main configuration file in the solution root:

```json
{
  "ConnectionStrings": {
    "nocturne": "Host=localhost;Port=5432;Database=nocturne;Username=nocturne;Password=nocturne"
  },
  "Authentication": {
    "ApiSecret": "your-secret-here",
    "JwtKey": "your-jwt-signing-key",
    "JwtIssuer": "Nocturne",
    "JwtAudience": "NightscoutClient"
  }
}
```

### Environment Variables

Override configuration using environment variables:

```bash
ConnectionStrings__nocturne="Host=mydb;..."
Authentication__ApiSecret="my-secret"
ASPNETCORE_ENVIRONMENT=Production
```

## Data Connectors

Nocturne includes native connectors for popular diabetes platforms:

| Connector            | Description                          | Status    |
| -------------------- | ------------------------------------ | --------- |
| **Dexcom Share**     | Dexcom CGM data via Share API        | Supported |
| **Glooko**           | Comprehensive diabetes data platform | Supported |
| **LibreLinkUp**      | FreeStyle Libre glucose readings     | Supported |
| **MiniMed CareLink** | Medtronic pump and sensor data       | Supported |
| **MyFitnessPal**     | Food and nutrition tracking          | Supported |
| **Nightscout**       | Nightscout-to-Nightscout sync        | Supported |

### Using Connectors

```bash
# Interactive setup wizard
cd src/Tools
dotnet run --project Nocturne.Tools.Connect init --interactive

# Test connections
dotnet run --project Nocturne.Tools.Connect test --all

# Run in daemon mode
dotnet run --project Nocturne.Tools.Connect run --daemon
```

## Development

### Running Tests

```bash
# Run all tests
dotnet test

# Run unit tests only
dotnet test --filter "Category!=Integration&Category!=Performance"

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
```

### Database Migrations

```bash
# Create a new migration
cd src/Infrastructure/Nocturne.Infrastructure.Data
dotnet ef migrations add YourMigrationName

# Apply migrations
dotnet ef database update
```

## Tools

### Nocturne Connect

Data connector management and synchronization.

```bash
dotnet run --project src/Tools/Nocturne.Tools.Connect -- --help
```

### Migration Tool

Migrate data from MongoDB or Nightscout API to PostgreSQL.

```bash
dotnet run --project src/Tools/Nocturne.Tools.Migration -- copy \
  --source-api "https://nightscout.example.com" \
  --target-connection "Host=localhost;..."
```

### Configuration Generator

Generate example configuration files.

```bash
dotnet run --project src/Tools/Nocturne.Tools.Config -- generate \
  --format json --output appsettings.example.json
```

### MCP Server

Model Context Protocol server for AI integration.

```bash
dotnet run --project src/Tools/Nocturne.Tools.McpServer -- server
```

See [src/Tools/README.md](src/Tools/README.md) for detailed tool documentation.

## Deployment

### Azure Container Apps

```bash
# Install Azure Developer CLI
curl -fsSL https://aka.ms/install-azd.sh | bash

# Deploy to Azure
azd auth login
azd init
azd up
```

### Docker Compose

```bash
docker-compose build
docker-compose up -d
```

## API Documentation

### Interactive Documentation

- **Scalar UI**: http://localhost:1612/scalar
- **Swagger UI**: http://localhost:1612/swagger
- **OpenAPI JSON**: http://localhost:1612/openapi/v1.json

### Key Endpoints

```
GET    /api/v1/entries          # Glucose entries
POST   /api/v1/entries
GET    /api/v1/treatments       # Treatments
POST   /api/v1/treatments
GET    /api/v1/devicestatus     # Device status
GET    /api/v1/profile          # Profile settings
GET    /api/v2/properties       # Statistics
WS     /hubs/data               # Real-time SignalR hub
```

## License

This project is licensed under the MIT License.

## Disclaimer

Nocturne is a community project and is not affiliated with or endorsed by the Nightscout Project, Abbott, Dexcom, Medtronic, Glooko, or MyFitnessPal.

**Important:** This software is provided as-is for personal use. Always verify glucose readings with approved medical devices. Never make treatment decisions based solely on data from this application.

## Acknowledgments

- Built on the shoulders of the [Nightscout Project](https://github.com/nightscout/cgm-remote-monitor)
- Powered by [.NET 9](https://dotnet.microsoft.com/) and [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/)
