# Nocturne Tools

A collection of modern C# tools built with the Spectre.Console CLI framework for managing Nocturne infrastructure, configuration, and external service integration.

## 🛠️ Available Tools

### MCP Server

**Path:** `Nocturne.Tools.McpServer`

A Model Context Protocol (MCP) server providing AI and automation tools for interacting with Nocturne glucose data APIs. Supports both stdio and Server-Sent Events (SSE) transports.

## 🌟 Features

### Shared Infrastructure

- **🔧 Spectre.Console Framework**: All tools built with modern Spectre.Console CLI framework for consistent user experience
- **📊 Progress Reporting**: Real-time progress tracking across all operations
- **🛡️ Type-Safe Configuration**: Comprehensive validation with helpful error messages
- **📝 Structured Logging**: Consistent logging patterns with configurable verbosity
- **⚙️ Dependency Injection**: Modern .NET patterns for maintainable, testable code

### MCP Server

- **🤖 AI Integration**: Model Context Protocol server for AI tool integration
- **🚀 Dual Transport**: Supports both stdio and Server-Sent Events (SSE) transports
- **📊 Glucose Tools**: Comprehensive glucose data analysis and management tools
- **🔄 Backward Compatibility**: Legacy command-line argument support
- **🌐 Web Interface**: Optional web interface with health checks and status endpoints

## 🚀 Quick Start

### Prerequisites

- **.NET 9.0 or higher** - [Download here](https://dotnet.microsoft.com/download)
- **Nocturne instance** - Your target Nocturne API (for the MCP tool)

### Installation & Setup

1. **Clone and build the project:**

   ```bash
   git clone <repository-url>
   cd nocturne/src/Tools
   dotnet build
   ```

2. **Run the tool with help to see available commands:**

   ```bash
   dotnet run --project Nocturne.Tools.McpServer --help
   ```

### Common Commands

#### MCP Server

```bash
# Start with stdio transport (default)
dotnet run --project Nocturne.Tools.McpServer server

# Start with web/SSE transport
dotnet run --project Nocturne.Tools.McpServer server --web --port 5000

# With custom API URL
dotnet run --project Nocturne.Tools.McpServer server \
  --api-url "http://localhost:1612" \
  --verbose
```

## 📋 Command Reference

All tools are built with the Spectre.Console CLI framework and provide consistent help and command structure.

### MCP Server Commands

| Command   | Description              | Examples                   |
| --------- | ------------------------ | -------------------------- |
| `server`  | Start MCP server         | `server --web --port 5000` |
| `version` | Show version information | `version --detailed`       |

### Available MCP Tools (when server is running)

| Tool                    | Description                               |
| ----------------------- | ----------------------------------------- |
| `GetCurrentEntry`       | Get the most recent glucose reading       |
| `GetRecentEntries`      | Get recent glucose entries with filtering |
| `GetEntriesByDateRange` | Get entries within a specific date range  |
| `GetEntryById`          | Get a specific entry by ID                |
| `CreateEntry`           | Create a new glucose entry                |
| `GetGlucoseStatistics`  | Get glucose statistics and time in range  |
| `GetEntryCount`         | Get entry count statistics                |

### Detailed Command Usage

#### MCP Server Examples

```bash
# Start with stdio transport (for console-based MCP clients)
dotnet run --project Nocturne.Tools.McpServer server

# Start with SSE transport (for web-based MCP clients)
dotnet run --project Nocturne.Tools.McpServer server \
  --web \
  --port 5000 \
  --api-url "http://localhost:1612" \
  --verbose

# Start with custom configuration
dotnet run --project Nocturne.Tools.McpServer server \
  --config "mcp-config.json" \
  --timeout 60

# Get version and capabilities
dotnet run --project Nocturne.Tools.McpServer version --detailed
```

## ⚙️ Configuration

Each tool uses a modern configuration system with type-safe validation and helpful error messages.

### Configuration Methods

All tools support multiple configuration methods:

1. **Command-line arguments** (highest priority)
2. **Configuration files** (JSON, YAML, Environment Variables)
3. **Environment variables**
4. **Default values** (lowest priority)

### Tool-Specific Configuration

#### MCP Server Configuration

The MCP server supports both command-line and configuration file options:

```bash
# Command-line configuration
dotnet run --project Nocturne.Tools.McpServer server \
  --api-url "http://localhost:1612" \
  --port 5000 \
  --verbose

# Or use a configuration file
dotnet run --project Nocturne.Tools.McpServer server \
  --config "mcp-config.json"
```

### Global Options

All tools support these common options:

- `--help` - Show detailed help information
- `--version` - Display version information
- `--verbose` - Enable detailed logging
- `--config <path>` - Use custom configuration file

## 🔧 Troubleshooting

### Common Issues

**Authentication Failures:**

- Use configuration validation commands to check credentials
- Verify API endpoints are accessible
- Check API secrets and connection strings

**MCP Server Issues:**

- Check that the Nocturne API is running and accessible
- Verify port availability for SSE transport mode
- Use `--verbose` for detailed MCP protocol logging
- Test API connectivity before starting the server

### Getting Help

1. **Built-in Help:** All tools support `--help` with detailed usage information
2. **Version Information:** Use `version --detailed` for comprehensive system info
3. **Verbose Logging:** Add `--verbose` to any command for detailed output
4. **Configuration Validation:** Each tool has built-in validation commands
5. **Progress Reporting:** All tools provide real-time progress information

## 🔄 Deployment Options

### Docker Deployment

Example Dockerfile for any tool:

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY src/Tools ./Tools
COPY *.sln ./
RUN dotnet publish Tools/Nocturne.Tools.McpServer -c Release -o out

ENTRYPOINT ["dotnet", "out/Nocturne.Tools.McpServer.dll", "server"]
```

### MCP Server Deployment

The MCP server supports both console and web deployment modes:

```bash
# Console mode (stdio transport)
dotnet run --project Nocturne.Tools.McpServer server

# Web mode (SSE transport)
dotnet run --project Nocturne.Tools.McpServer server --web --port 5000
```

## 🏗️ Development

### Building from Source

```bash
# Clone repository
git clone <repository-url>
cd nocturne/src/Tools

# Restore dependencies
dotnet restore

# Build all tools
dotnet build

# Run specific tool
dotnet run --project Nocturne.Tools.McpServer --help
```

### Project Architecture

The tools follow a modern, layered architecture:

```
src/Tools/
├── Nocturne.Tools.Abstractions/    # Shared interfaces and contracts
│   ├── Commands/                   # Command interfaces
│   ├── Configuration/              # Configuration interfaces
│   └── Services/                   # Service interfaces
├── Nocturne.Tools.Core/           # Shared implementation
│   ├── Commands/                  # Base command classes
│   ├── Services/                  # Common services
│   └── SpectreApplicationBuilder.cs # Spectre.Console extensions
└── Nocturne.Tools.McpServer/      # MCP server
```

## 📄 License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.

## 🤝 Contributing

Contributions are welcome! Please:

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

For major changes, please open an issue first to discuss what you would like to change.

---

**Note:** This is a community project and is not affiliated with Abbott, Medtronic, Dexcom, Glooko, or Nightscout. Use at your own risk and always verify data accuracy.
