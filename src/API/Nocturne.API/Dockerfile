# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy central package management file
COPY ["Directory.Packages.props", "."]

# Copy csproj files and restore dependencies
COPY ["src/API/Nocturne.API/Nocturne.API.csproj", "src/API/Nocturne.API/"]
COPY ["src/Core/Nocturne.Core.Constants/Nocturne.Core.Constants.csproj", "src/Core/Nocturne.Core.Constants/"]
COPY ["src/Core/Nocturne.Core.Contracts/Nocturne.Core.Contracts.csproj", "src/Core/Nocturne.Core.Contracts/"]
COPY ["src/Core/Nocturne.Core.Models/Nocturne.Core.Models.csproj", "src/Core/Nocturne.Core.Models/"]
COPY ["src/Infrastructure/Nocturne.Infrastructure.Cache/Nocturne.Infrastructure.Cache.csproj", "src/Infrastructure/Nocturne.Infrastructure.Cache/"]
COPY ["src/Infrastructure/Nocturne.Infrastructure.Data/Nocturne.Infrastructure.Data.csproj", "src/Infrastructure/Nocturne.Infrastructure.Data/"]
COPY ["src/Aspire/Nocturne.Aspire.ServiceDefaults/Nocturne.Aspire.ServiceDefaults.csproj", "src/Aspire/Nocturne.Aspire.ServiceDefaults/"]
COPY ["src/Connectors/Nocturne.Connectors.Core/Nocturne.Connectors.Core.csproj", "src/Connectors/Nocturne.Connectors.Core/"]
COPY ["src/Connectors/Nocturne.Connectors.Dexcom/Nocturne.Connectors.Dexcom.csproj", "src/Connectors/Nocturne.Connectors.Dexcom/"]
COPY ["src/Connectors/Nocturne.Connectors.Glooko/Nocturne.Connectors.Glooko.csproj", "src/Connectors/Nocturne.Connectors.Glooko/"]
COPY ["src/Connectors/Nocturne.Connectors.FreeStyle/Nocturne.Connectors.FreeStyle.csproj", "src/Connectors/Nocturne.Connectors.FreeStyle/"]
COPY ["src/Connectors/Nocturne.Connectors.MiniMed/Nocturne.Connectors.MiniMed.csproj", "src/Connectors/Nocturne.Connectors.MiniMed/"]
COPY ["src/Connectors/Nocturne.Connectors.Nightscout/Nocturne.Connectors.Nightscout.csproj", "src/Connectors/Nocturne.Connectors.Nightscout/"]
COPY ["src/Connectors/Nocturne.Connectors.MyFitnessPal/Nocturne.Connectors.MyFitnessPal.csproj", "src/Connectors/Nocturne.Connectors.MyFitnessPal/"]

RUN dotnet restore "src/API/Nocturne.API/Nocturne.API.csproj"

# Copy the rest of the source code
COPY . .

# Build and publish
WORKDIR "/src/src/API/Nocturne.API"
RUN dotnet build "Nocturne.API.csproj" -c Release -o /app/build
RUN dotnet publish "Nocturne.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Configuration files are already included in the publish output
# Root appsettings.json files are copied via project references

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Install curl for health checks
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 1612

ENV ASPNETCORE_URLS=http://+:1612

ENTRYPOINT ["dotnet", "Nocturne.API.dll"]
