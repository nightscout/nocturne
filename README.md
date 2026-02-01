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
- **Data Connectors** - Dexcom Share, Glooko, LibreLinkUp, MiniMed CareLink, MyFitnessPal, Nightscout, and MyLife
- **PostgreSQL** - Modern relational database with EF Core migrations
- **Observability** - OpenTelemetry integration for monitoring (Soon)
- **Containerized** - Docker support for all services

## Quick Start with .NET Aspire (Development)

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- [Docker Desktop](https://www.docker.com/products/docker-desktop)
- [NodeJS](https://nodejs.org/)
- [pnpm](https://pnpm.io/)

Copy the appsettings.example.json, and rename it to `appsettings.json`. Fill in the values for the connection strings, and any other settings you want to change. If you'd like to pipe in your Nightscout values into it just to test it out, do so in the `Connector.Nightscout` section, *not* the CompatibilityProxy; they are fundamentally different things.

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
- Click on the link in the console, which will have  Open the Aspire dashboard at `[http://localhost:17257](https://localhost:17257/)`

You can then access the frontend from the port assigned to it.

### Access the API

Once Aspire starts:

- **API**: https://localhost:1612
- **API Documentation**: https://localhost:1612/scalar
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

#### Environment Variables

Override configuration using environment variables:

```bash
ConnectionStrings__nocturne="Host=mydb;..."
Authentication__ApiSecret="my-secret"
ASPNETCORE_ENVIRONMENT=Production
```

You generally shouldn't have to do this, **ever** during development- configuration lives in the appsettings, and is automagically passed through.

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
| **MyLife**           | Syncing for MyLife / CamAPS FX       | Supported |

### Using Connectors

If you set up the connector's settings in the appsettings, then it'll automatically start when you run `aspire run`.


## Quick Start with Docker (For prod)

Run `aspire publish` anywhere within the repository. This will use the `appsettings.json` and create a `docker-compose.yml` and `.env` file within `./aspire-output` which you can then use. You may need to replace the image .env like so: 

```bash
    NOCTURNE_API_IMAGE=ghcr.io/nightscout/nocturne/nocturne-api:latest
    NOCTURNE_WEB_IMAGE=ghcr.io/nightscout/nocturne/nocturne-web:latest
    NIGHTSCOUT_CONNECTOR_IMAGE=ghcr.io/nightscout/nocturne/nightscout-connector:latest
    GLOOKO_CONNECTOR_IMAGE=ghcr.io/nightscout/nocturne/glooko-connector:latest
```

We're working on a tool to automate this and enable easier deployment via the web. 

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

### Migration Tool

Migrate data from MongoDB or Nightscout API to PostgreSQL.

```bash
dotnet run --project src/Tools/Nocturne.Tools.Migration -- copy \
  --source-api "https://nightscout.example.com" \
  --target-connection "Host=localhost;..."
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

- **Scalar UI**: https://localhost:1612/scalar
- **OpenAPI JSON**: https://localhost:1612/openapi/v1.json

### Key Endpoints

Nocturne aims to match Nightscout's API 1:1, so any Nightscout API endpoint should be usable. Nocturne-only endpoints are scoped to v4.

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
- Powered by [.NET 10](https://dotnet.microsoft.com/) and [.NET Aspire](https://learn.microsoft.com/en-us/dotnet/aspire/)
