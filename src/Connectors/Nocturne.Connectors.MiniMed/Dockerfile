# Use the ASP.NET Core runtime as base image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Use the .NET SDK for building
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy central package management file
COPY ["Directory.Packages.props", "."]

# Copy project files
COPY ["src/Connectors/Nocturne.Connectors.MiniMed/Nocturne.Connectors.MiniMed.csproj", "src/Connectors/Nocturne.Connectors.MiniMed/"]
COPY ["src/Connectors/Nocturne.Connectors.Core/Nocturne.Connectors.Core.csproj", "src/Connectors/Nocturne.Connectors.Core/"]
COPY ["src/Core/Nocturne.Core.Models/Nocturne.Core.Models.csproj", "src/Core/Nocturne.Core.Models/"]
COPY ["src/Aspire/Nocturne.Aspire.ServiceDefaults/Nocturne.Aspire.ServiceDefaults.csproj", "src/Aspire/Nocturne.Aspire.ServiceDefaults/"]

# Restore dependencies
RUN dotnet restore "src/Connectors/Nocturne.Connectors.MiniMed/Nocturne.Connectors.MiniMed.csproj"

# Copy source code
COPY . .
WORKDIR "/src/src/Connectors/Nocturne.Connectors.MiniMed"

# Build the application
RUN dotnet build "Nocturne.Connectors.MiniMed.csproj" -c $BUILD_CONFIGURATION -o /app/build

# Publish the application
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Nocturne.Connectors.MiniMed.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final stage
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create non-root user for security
RUN adduser --disabled-password --gecos "" --shell /bin/bash --uid 1001 appuser && chown -R appuser /app
USER appuser

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Nocturne.Connectors.MiniMed.dll"]