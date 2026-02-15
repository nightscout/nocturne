# Nocturne API

A high-performance C# rewrite of the Nightscout API with full feature parity.

## What is this?

Nocturne is a modern C# implementation of the complete Nightscout API. It runs natively without requiring a legacy
Nightscout installation, providing all the same endpoints and functionality you expect from Nightscout.

**Key Features:**

- ✅ **Complete API Implementation** - All Nightscout endpoints implemented natively in C#
- 🚀 **High Performance** - Optimized for speed and efficiency
- 📚 **MongoDB Integration** - Direct database access with optimized queries
- 🔐 **Full Authentication** - Complete support for Nightscout's authentication system
- 📝 **API Documentation** - Built-in interactive documentation at `/scalar`
- 🔄 **Compatibility Proxy** - Optional A/B testing proxy for gradual migration from Nightscout (disabled by default)

## Quick Start

**Prerequisites:**

- .NET 10.0 or later
- Postgres database

**Option 1: VS Code Tasks (Recommended for development)**

Press `Ctrl+Shift+P` and search for "Tasks: Run Task", then select:

- `start-api` - Starts the native Nocturne API

**Option 2: Command Line**

```bash
cd src/API/Nocturne.API
dotnet run
```

The API will start on `http://localhost:1612` and serve all endpoints natively.

## Native API Endpoints

Nocturne implements all major Nightscout API endpoints natively:

### Status & Information

- `GET /api/v1/status` - Server status and configuration
- `GET /api/versions` - Supported API versions
- `GET /api/v3/version` - API version information
- `GET /api/v3/status` - Extended status information
- `GET /api/v3/lastmodified` - Last modified timestamps

### Data Endpoints

- `GET/POST/PUT/DELETE /api/v1/entries` - Blood glucose entries
- `GET/POST/PUT/DELETE /api/v1/treatments` - Treatments and boluses
- `GET/POST/PUT/DELETE /api/v1/devicestatus` - Device status entries
- `GET/POST/PUT/DELETE /api/v1/profile` - Treatment profiles
- `GET/POST/PUT/DELETE /api/v1/food` - Food database

### Query & Analysis

- `GET /api/v1/count/*` - Count entries with filters
- `GET /api/v1/times` - Time-based queries
- `GET /api/v1/slice` - Data slicing operations
- `GET /api/v2/ddata` - Data processing and calculations
- `GET /api/v2/properties` - Data properties and metadata
- `GET /api/v2/summary` - Summary statistics

### Notifications & Alerts

- `GET/POST /api/v2/notifications` - Notification management
- `GET /api/v2/authorization` - Authorization and permissions

## Configuration

### MongoDB Connection

Configure your MongoDB connection in `appsettings.json`:

```json
{
  "MongoDb": {
    "ConnectionString": "mongodb://localhost:27017/nightscout",
    "DatabaseName": "nightscout",
    "EntriesCollectionName": "entries",
    "TreatmentsCollectionName": "treatments",
    "DeviceStatusCollectionName": "devicestatus",
    "ProfilesCollectionName": "profile",
    "FoodCollectionName": "food"
  }
}
```

### Authentication

Configure JWT authentication:

```json
{
  "JwtSettings": {
    "SecretKey": "YourSecretKeyHere",
    "Issuer": "Nocturne",
    "Audience": "NightscoutClient",
    "ExpirationHours": 24
  }
}
```

## Troubleshooting

**MongoDB Connection Issues:**

- Verify your MongoDB connection string is correct
- Ensure MongoDB is running and accessible
- Check that the database name matches your Nightscout database

**Authentication errors:**

- Verify your API_SECRET environment variable is set
- Check JWT configuration in appsettings.json
- Ensure your client is sending proper authentication headers

**Missing data:**

- Verify your MongoDB collections contain data
- Check collection names in configuration match your database
- Ensure your MongoDB user has proper read/write permissions

**Performance issues:**

- Monitor MongoDB query performance
- Check system resources (CPU/Memory)
- Review logging output for slow queries

**Enable debug logging:**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Nocturne": "Debug"
    }
  }
}
```

## Compatibility Proxy (Migration Support)

If you're migrating from an existing Nightscout instance, you can enable the Compatibility Proxy to run both systems
side-by-side and compare responses:

```json
{
  "Parameters": {
    "CompatibilityProxy": {
      "NightscoutUrl": "https://your-existing-nightscout.com",
      "DefaultStrategy": "Nightscout",
      "EnableDetailedLogging": true,
      "Comparison": {
        "EnableDeepComparison": true
      }
    }
  }
}
```

This allows you to "try before you buy" - point your data sources at Nocturne, which will forward requests to your
existing Nightscout while comparing responses to ensure compatibility.

**Note:** The Compatibility Proxy is intended for migration testing. Once you're confident in Nocturne, disable it for
optimal performance.

## Development

Nocturne follows Clean Architecture principles with:

- **API Layer**: Controllers and HTTP handling
- **Application Layer**: Business logic and use cases
- **Domain Layer**: Core models and interfaces
- **Infrastructure Layer**: Data access and external services

To contribute or extend Nocturne, see the main project documentation.

## TypeScript Client Generation

The API automatically generates TypeScript client code using NSwag whenever the project is built in Debug mode.

**Generated Files:**

- `../../Web/Nocturne.Web/src/lib/api/generated/nocturne-api-client.ts` - Auto-generated TypeScript client

**Usage in Frontend:**

```typescript
import { apiClient } from "$lib/api/api-client";

// The client is automatically typed with all your API endpoints
const entries = await apiClient.rawClient.getEntries();
const treatments = await apiClient.rawClient.getTreatments();
```
