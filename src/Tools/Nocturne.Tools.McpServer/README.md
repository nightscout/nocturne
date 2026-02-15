# Nocturne MCP Server

This is a Model Context Protocol (MCP) server for the Nocturne API, allowing AI agents to interact with glucose data and
other Nocturne API features.

## Overview

The MCP server provides tools for AI agents to:

- Retrieve current and historical glucose readings
- Get glucose statistics and analytics
- Create new glucose entries
- Query entries by date ranges and filters

## Prerequisites

- .NET 9.0 SDK or later
- Nocturne API running (default: http://localhost:1612)

## Quick Start

### 1. Building and Running Locally

```bash
# Build the project
dotnet build

# Run with stdio transport (default - for Claude Desktop)
dotnet run server

# Run with SSE transport (for remote hosting)
dotnet run server --web
# or with custom port
dotnet run server --web --port 8080

# Run with verbose logging
dotnet run server --verbose

# Get help
dotnet run --help
dotnet run server --help
```

### 2. Docker Setup

The MCP server supports **both stdio and SSE transports**:

```bash
# Build the Docker image
docker build -t nocturne-mcp-server .

# Run with stdio transport (interactive)
docker run -it --rm nocturne-mcp-server server

# Run with SSE transport (hosted web service)
docker run -p 8080:8080 --rm nocturne-mcp-server server --web

# Docker Compose options:
# Stdio mode (interactive)
docker-compose --profile mcp run --rm mcp-server

# SSE mode (hosted service)
docker-compose --profile mcp-web up mcp-server-web
```

### 3. Configuration

The server automatically inherits configuration from the root Nocturne project and supports multiple configuration
sources:

1. **Environment Variables** (highest priority):
   ```bash
   export NOCTURNE_API_URL=http://localhost:1612
   ```

2. **Local appsettings.json** (medium priority):
   ```json
   {
     "NocturneApi": {
       "BaseUrl": "http://localhost:1612",
       "TimeoutSeconds": 30
     }
   }
   ```

3. **Root project appsettings.json** (lowest priority):
    - Automatically loads from `../../../../appsettings.json`

**Default API URL**: `http://localhost:1612` (if no configuration is provided)

## Available Tools

### Entry Management Tools

1. **GetCurrentEntry** - Get the most recent glucose reading
2. **GetRecentEntries** - Get recent glucose entries with optional filtering
3. **GetEntriesByDateRange** - Get entries within a specific date range
4. **GetEntryById** - Get a specific entry by ID
5. **CreateEntry** - Create a new glucose entry
6. **GetGlucoseStatistics** - Get comprehensive glucose statistics including time in range
7. **GetEntryCount** - Get count statistics by type and time period

### Example MCP Client Usage

Once the server is running, AI agents can use these tools to:

- **Monitor current glucose**: "What's my current glucose reading?"
- **Analyze trends**: "Show me glucose statistics for the last 24 hours"
- **Review history**: "Get my glucose readings from yesterday"
- **Add entries**: "Add a new glucose reading of 120 mg/dL"

## Tool Parameters

### GetRecentEntries

- `count` (optional): Number of entries to retrieve (default: 24)
- `type` (optional): Entry type filter ("sgv", "mbg", "cal")

### GetEntriesByDateRange

- `startDate`: Start date in ISO 8601 format (e.g., "2024-01-01T00:00:00Z")
- `endDate`: End date in ISO 8601 format
- `type` (optional): Entry type filter

### GetGlucoseStatistics

- `hours` (optional): Number of hours to analyze (default: 24)
- `type` (optional): Entry type to analyze (default: "sgv")

### CreateEntry

- `glucose`: Glucose value in mg/dL
- `type` (optional): Entry type (default: "sgv")
- `direction` (optional): Trend direction
- `device` (optional): Device identifier
- `dateTime` (optional): Date/time in ISO 8601 format (defaults to now)
- `notes` (optional): Additional notes

## Entry Types

- **sgv**: Sensor glucose values (CGM readings)
- **mbg**: Meter blood glucose (fingerstick readings)
- **cal**: Calibration entries

## Glucose Trend Directions

- `Flat`: No significant change
- `SingleUp`: Rising slowly
- `DoubleUp`: Rising quickly
- `SingleDown`: Falling slowly
- `DoubleDown`: Falling quickly
- `FortyFiveUp`: Rising at moderate rate
- `FortyFiveDown`: Falling at moderate rate

## Transport Options

### Stdio Transport (Default)

**Best for**: Local Claude Desktop, VS Code, development

- **AI Clients manage the process**: Claude Desktop, VS Code, etc. spawn the MCP server as a child process
- **Bidirectional communication**: JSON-RPC messages flow over stdin/stdout
- **Security**: No network ports exposed, process isolation
- **Simplicity**: Direct process communication without network complexity

### SSE Transport (Server-Sent Events)

**Best for**: Remote hosting, cloud deployment, shared servers

- **Web-based**: Runs as HTTP server with `/sse` endpoint
- **Remote access**: Can be hosted on separate machines/cloud
- **Persistent connection**: Uses Server-Sent Events for real-time communication
- **Scalable**: Multiple clients can connect to same server instance

### When to Use Each:

| Scenario               | Transport | Command                   |
|------------------------|-----------|---------------------------|
| Claude Desktop (local) | stdio     | `dotnet run server`       |
| VS Code (local)        | stdio     | `dotnet run server`       |
| Remote Claude Desktop  | SSE       | `dotnet run server --web` |
| Cloud deployment       | SSE       | `dotnet run server --web` |
| Shared team server     | SSE       | `dotnet run server --web` |

## MCP Client Integration

### Claude Desktop Setup

To use this MCP server with Claude Desktop, follow these steps:

#### 1. Install Prerequisites

- **Windows**: Install .NET 9.0 SDK from [Microsoft](https://dotnet.microsoft.com/download/dotnet/9.0)
- **macOS**: Install via Homebrew: `brew install dotnet`
- **Linux**: Follow [Microsoft's Linux installation guide](https://docs.microsoft.com/en-us/dotnet/core/install/linux)

#### 2. Configure Claude Desktop

Add the following configuration to your Claude Desktop MCP settings file:

**Windows**: `%APPDATA%\Claude\claude_desktop_config.json`
**macOS/Linux**: `~/.config/claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "nocturne": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "C:\\path\\to\\nocturne\\src\\Tools\\Nocturne.Tools.McpServer\\Nocturne.Tools.McpServer.csproj",
        "server"
      ],
      "env": {
        "NOCTURNE_API_URL": "http://localhost:1612"
      }
    }
  }
}
```

**Important**: Replace `C:\\path\\to\\nocturne` with the actual path to your Nocturne repository.

#### 3. Alternative Setup (Pre-built Binary)

For better performance, you can build and use a self-contained binary:

```bash
# Build self-contained executable
cd src/Tools/Nocturne.Tools.McpServer
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Update Claude Desktop config to use the binary
```

```json
{
  "mcpServers": {
    "nocturne": {
      "command": "C:\\path\\to\\nocturne\\src\\Tools\\Nocturne.Tools.McpServer\\bin\\Release\\net9.0\\win-x64\\publish\\Nocturne.Tools.McpServer.exe",
      "env": {
        "NOCTURNE_API_URL": "http://localhost:1612"
      }
    }
  }
}
```

#### 4. Verify Setup

1. Ensure your Nocturne API is running on `http://localhost:1612`
2. Start Claude Desktop
3. Look for the "nocturne" server in Claude Desktop's MCP status
4. Test with queries like: "What's my current glucose reading?"

#### 5. Troubleshooting

- **Server won't start**: Check that .NET 9.0 is installed and in PATH
- **Connection errors**: Verify Nocturne API is running and accessible
- **Path issues**: Use absolute paths in the configuration
- **Permissions**: Ensure Claude Desktop can execute dotnet commands

### VS Code MCP Extension

**Stdio Transport (Local):**

```json
{
  "servers": {
    "nocturne-local": {
      "type": "stdio",
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "path/to/nocturne/src/Tools/Nocturne.Tools.McpServer/Nocturne.Tools.McpServer.csproj",
        "server"
      ],
      "env": {
        "NOCTURNE_API_URL": "http://localhost:1612"
      }
    }
  }
}
```

**SSE Transport (Remote):**

```json
{
  "servers": {
    "nocturne-remote": {
      "type": "sse",
      "url": "http://your-server.com:8080/sse"
    }
  }
}
```

### Claude Desktop SSE Configuration

For **remote/hosted** MCP servers using SSE transport:

```json
{
  "mcpServers": {
    "nocturne-remote": {
      "type": "sse",
      "url": "http://your-server.com:8080/sse"
    }
  }
}
```

### Remote Deployment Examples

**Docker on cloud server:**

```bash
# On your cloud server
docker run -d -p 8080:8080 \
  -e NOCTURNE_API_URL=http://your-nocturne-api:1612 \
  nocturne-mcp-server server --web

# Client connects to: http://your-server.com:8080/sse
```

**Kubernetes deployment:**

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: nocturne-mcp-server
spec:
  replicas: 1
  selector:
    matchLabels:
      app: nocturne-mcp-server
  template:
    metadata:
      labels:
        app: nocturne-mcp-server
    spec:
      containers:
      - name: mcp-server
        image: nocturne-mcp-server
        args: ["server", "--web"]
        ports:
        - containerPort: 8080
        env:
        - name: NOCTURNE_API_URL
          value: "http://nocturne-api:1612"
---
apiVersion: v1
kind: Service
metadata:
  name: nocturne-mcp-server
spec:
  selector:
    app: nocturne-mcp-server
  ports:
  - port: 80
    targetPort: 8080
  type: LoadBalancer
```

### Other MCP Clients

This server uses the standard MCP protocol and can be integrated with any MCP-compatible AI client or agent framework.
The server supports both **stdio** and **SSE** transports for maximum flexibility.

## Error Handling

All tools include comprehensive error handling and will return descriptive error messages if:

- The Nocturne API is unavailable
- Invalid parameters are provided
- Network issues occur
- Data validation fails

## Security Considerations

- The MCP server assumes it's running in a trusted environment
- No authentication is currently implemented - ensure the Nocturne API has appropriate security measures
- Consider firewall rules and network segmentation for production deployments
