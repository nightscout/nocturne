# Nocturne Tools

Shared infrastructure for Spectre.Console command-line tools.

```
src/Tools/
├── Nocturne.Tools.Abstractions/    # Command, configuration and service interfaces
└── Nocturne.Tools.Core/            # Base commands, common services, SpectreApplicationBuilder
```

The MCP server is part of the API: `POST /mcp` on a tenant host, behind the tenant's OAuth
server. See `src/API/Nocturne.API/Mcp/`.
