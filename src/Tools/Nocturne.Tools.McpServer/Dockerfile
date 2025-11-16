# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy central package management file
COPY ["Directory.Packages.props", "."]

# Copy project files
COPY ["src/Tools/Nocturne.Tools.McpServer/Nocturne.Tools.McpServer.csproj", "src/Tools/Nocturne.Tools.McpServer/"]
COPY ["src/Core/Nocturne.Core.Models/Nocturne.Core.Models.csproj", "src/Core/Nocturne.Core.Models/"]
COPY ["src/Core/Nocturne.Core.Contracts/Nocturne.Core.Contracts.csproj", "src/Core/Nocturne.Core.Contracts/"]

# Restore dependencies
RUN dotnet restore "src/Tools/Nocturne.Tools.McpServer/Nocturne.Tools.McpServer.csproj"

# Copy source code
COPY . .

# Build the application
WORKDIR "/src/src/Tools/Nocturne.Tools.McpServer"
RUN dotnet build "Nocturne.Tools.McpServer.csproj" -c Release -o /app/build

# Publish the application
RUN dotnet publish "Nocturne.Tools.McpServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

# Copy the published application
COPY --from=build /app/publish .

# Copy configuration files
COPY ["appsettings.json", "./"]

# Create non-root user for security
RUN adduser --disabled-password --gecos '' mcpuser && chown -R mcpuser /app
USER mcpuser

# Set environment variables
ENV ASPNETCORE_ENVIRONMENT=Production
ENV NOCTURNE_API_URL=http://nocturne-api:1612
ENV ASPNETCORE_URLS=http://+:8080

# Expose port for SSE transport (when using --web flag)
EXPOSE 8080

# Health check for SSE mode, fallback for stdio mode
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || dotnet --version || exit 1

# Default to stdio transport, use --web for SSE transport
# Examples:
# docker run -it nocturne-mcp-server (stdio mode)
# docker run -p 8080:8080 nocturne-mcp-server --web (SSE mode)
ENTRYPOINT ["dotnet", "Nocturne.Tools.McpServer.dll"]