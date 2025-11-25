using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Get the solution root directory
        var solutionRoot = Path.GetFullPath(
            Path.Combine(builder.AppHostDirectory, "..", "..", "..")
        );

        // Load appsettings from solution root
        builder.Configuration.AddJsonFile(
            Path.Combine(solutionRoot, "appsettings.json"),
            optional: true,
            reloadOnChange: true
        );
        builder.Configuration.AddJsonFile(
            Path.Combine(solutionRoot, $"appsettings.{builder.Environment.EnvironmentName}.json"),
            optional: true,
            reloadOnChange: true
        );

        // Configure Docker Compose environment for CI/CD and production deployments
        // This enables Aspire to generate docker-compose.yml with proper registry and build settings
        if (builder.ExecutionContext.IsPublishMode)
        {
            var containerRegistry = builder.Configuration["ContainerRegistry"] ?? "docker.io";
            var containerRepository = builder.Configuration["ContainerRepository"] ?? "nocturne";

            builder
                .AddDockerComposeEnvironment("production")
                .WithProperties(env =>
                {
                    env.DefaultContainerRegistry = containerRegistry;
                    env.DefaultNetworkName = "nocturne-network";
                });

            Console.WriteLine($"[Aspire] Docker Compose environment configured:");
            Console.WriteLine($"[Aspire]   Registry: {containerRegistry}");
            Console.WriteLine($"[Aspire]   Repository: {containerRepository}");
            Console.WriteLine($"[Aspire]   Network: nocturne-network");
        }

        // Add PostgreSQL database - use remote database connection or local container
        var useRemoteDb = builder.Configuration.GetValue<bool>(
            "PostgreSql:UseRemoteDatabase",
            false
        );

        Console.WriteLine($"[Aspire] PostgreSql:UseRemoteDatabase: {useRemoteDb}");
        Console.WriteLine($"[Aspire] Environment: {builder.Environment.EnvironmentName}");

        // Use separate variables for managed vs remote database to maintain type safety
        // and make the distinction explicit between Aspire-managed containers and external databases
        IResourceBuilder<PostgresDatabaseResource>? managedDatabase = null;
        IResourceBuilder<IResourceWithConnectionString>? remoteDatabase = null;

        if (!useRemoteDb)
        {
            // Use local PostgreSQL container managed by Aspire
            // Parameters must have values from configuration or defaults
            var postgresUsername = builder.AddParameter(
                ServiceNames.Parameters.PostgresUsername,
                value: builder.Configuration["Parameters:postgres-username"]
                    ?? ServiceNames.Defaults.PostgresUser,
                secret: false
            );
            var postgresPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresPassword,
                value: builder.Configuration["Parameters:postgres-password"]
                    ?? ServiceNames.Defaults.PostgresPassword,
                secret: true
            );
            var postgresDbName = builder.AddParameter(
                ServiceNames.Parameters.PostgresDbName,
                value: builder.Configuration["Parameters:postgres-database"]
                    ?? ServiceNames.Defaults.PostgresDatabase,
                secret: false
            );

            Console.WriteLine(
                $"[Aspire] Creating local PostgreSQL container with username: {builder.Configuration["Parameters:postgres-username"] ?? ServiceNames.Defaults.PostgresUser}"
            );
            Console.WriteLine(
                $"[Aspire] Database name: {builder.Configuration["Parameters:postgres-database"] ?? ServiceNames.Defaults.PostgresDatabase}"
            );

            var postgres = builder
                .AddPostgres(ServiceNames.PostgreSql)
                .WithLifetime(ContainerLifetime.Persistent)
                .WithUserName(postgresUsername)
                .WithPassword(postgresPassword);

            // Only add PgAdmin in development to save resources
            if (builder.Environment.IsDevelopment())
            {
                postgres.WithPgAdmin();
            }

            postgres.WithDataVolume(ServiceNames.Volumes.PostgresData);

            managedDatabase = postgres.AddDatabase(
                ServiceNames.PostgreSql,
                builder.Configuration["Parameters:postgres-database"]
                    ?? ServiceNames.Defaults.PostgresDatabase
            );
            postgresUsername.WithParentRelationship(postgres);
            postgresPassword.WithParentRelationship(postgres);
            postgresDbName.WithParentRelationship(postgres);
        }
        else
        {
            // Use external/remote database via connection string reference
            // Read the connection string from Aspire Host's configuration
            var remoteConnectionString = builder.Configuration.GetConnectionString(
                ServiceNames.PostgreSql
            );

            if (string.IsNullOrWhiteSpace(remoteConnectionString))
            {
                throw new InvalidOperationException(
                    $"Remote database enabled but connection string '{ServiceNames.PostgreSql}' not found in Aspire Host configuration (ConnectionStrings section)."
                );
            }

            Console.WriteLine($"[Aspire] Remote database connection string loaded from config");
            Console.WriteLine(
                $"[Aspire] Connection: {remoteConnectionString.Substring(0, Math.Min(50, remoteConnectionString.Length))}..."
            );

            // AddConnectionString creates a resource that references the connection string from configuration
            remoteDatabase = builder.AddConnectionString(ServiceNames.PostgreSql);
        }

        // Add the Nocturne API service (without embedded connectors)
        // Aspire will auto-generate a Dockerfile during publish
        var api = builder
            .AddProject<Projects.Nocturne_API>(ServiceNames.NocturneApi)
            .WithExternalHttpEndpoints();

        // Configure database connection based on mode
        if (managedDatabase != null)
        {
            // For Aspire-managed local database, use WithReference which automatically injects the connection string
            Console.WriteLine("[Aspire] Configuring API with managed PostgreSQL database");
            api.WaitFor(managedDatabase).WithReference(managedDatabase);
        }
        else if (remoteDatabase != null)
        {
            // For external/remote database, use the connection string reference
            Console.WriteLine("[Aspire] Configuring API with remote PostgreSQL database");
            api.WithReference(remoteDatabase);
        }
        else
        {
            throw new InvalidOperationException(
                "Database configuration error: neither managed nor remote database was configured."
            );
        }

        // Add api-secret parameter for authentication (needed by connectors)
        var apiSecret = builder.AddParameter(ServiceNames.Parameters.ApiSecret, secret: true);

        // Add connector services as independent services based on Parameters configuration
        var connectors = builder.Configuration.GetSection("Parameters:Connectors");

        // Dexcom Connector Service
        var dexcomEnabled = connectors.GetValue<bool>("Dexcom:Enabled", false);
        if (dexcomEnabled)
        {
            var dexcomUsername = builder.AddParameter(
                "dexcom-username",
                value: builder.Configuration["Parameters:Connectors:Dexcom:Username"] ?? "",
                secret: true
            );
            var dexcomPassword = builder.AddParameter(
                "dexcom-password",
                value: builder.Configuration["Parameters:Connectors:Dexcom:Password"] ?? "",
                secret: true
            );
            var dexcomRegion = builder.AddParameter(
                "dexcom-region",
                value: builder.Configuration["Parameters:Connectors:Dexcom:Region"] ?? "",
                secret: false
            );
            var dexcomServer = builder.AddParameter(
                "dexcom-server",
                value: builder.Configuration["Parameters:Connectors:Dexcom:Server"] ?? "",
                secret: false
            );
            var dexcomSyncInterval = builder.AddParameter(
                "dexcom-sync-interval",
                value: builder.Configuration["Parameters:Connectors:Dexcom:SyncIntervalMinutes"]
                    ?? "",
                secret: false
            );

            var dexcom = builder
                .AddProject<Projects.Nocturne_Connectors_Dexcom>(ServiceNames.DexcomConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.DexcomPrefix}DexcomUsername",
                    dexcomUsername
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.DexcomPrefix}DexcomPassword",
                    dexcomPassword
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.DexcomPrefix}DexcomRegion",
                    dexcomRegion
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.DexcomPrefix}DexcomServer",
                    dexcomServer
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.DexcomPrefix}SyncIntervalMinutes",
                    dexcomSyncInterval
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.DexcomPrefix}ConnectSource",
                    ConnectSource.Dexcom.ToString()
                )
                .WithEnvironment("NocturneApiUrl", api.GetEndpoint("http"))
                .WithEnvironment("ApiSecret", apiSecret)
                .WaitFor(api)
                .WithReference(api);
        }

        // Glooko Connector Service
        var glookoEnabled = connectors.GetValue<bool>("Glooko:Enabled", false);
        if (glookoEnabled)
        {
            var glookoEmail = builder.AddParameter(
                "glooko-email",
                value: builder.Configuration["Parameters:Connectors:Glooko:Email"] ?? "",
                secret: true
            );
            var glookoPassword = builder.AddParameter(
                "glooko-password",
                value: builder.Configuration["Parameters:Connectors:Glooko:Password"] ?? "",
                secret: true
            );
            var glookoServer = builder.AddParameter(
                "glooko-server",
                value: builder.Configuration["Parameters:Connectors:Glooko:Server"] ?? "",
                secret: false
            );
            var glookoTimezoneOffset = builder.AddParameter(
                "glooko-timezone-offset",
                value: builder.Configuration["Parameters:Connectors:Glooko:TimezoneOffset"] ?? "",
                secret: false
            );
            var glookoSyncInterval = builder.AddParameter(
                "glooko-sync-interval",
                value: builder.Configuration["Parameters:Connectors:Glooko:SyncIntervalMinutes"]
                    ?? "",
                secret: false
            );

            var glooko = builder
                .AddProject<Projects.Nocturne_Connectors_Glooko>(ServiceNames.GlookoConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.GlookoPrefix}GlookoEmail",
                    glookoEmail
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.GlookoPrefix}GlookoPassword",
                    glookoPassword
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.GlookoPrefix}GlookoServer",
                    glookoServer
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.GlookoPrefix}GlookoTimezoneOffset",
                    glookoTimezoneOffset
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.GlookoPrefix}SyncIntervalMinutes",
                    glookoSyncInterval
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.GlookoPrefix}ConnectSource",
                    ConnectSource.Glooko.ToString()
                )
                .WithEnvironment("NocturneApiUrl", api.GetEndpoint("http"))
                .WithEnvironment("ApiSecret", apiSecret)
                .WaitFor(api)
                .WithReference(api);
        }

        // FreeStyle LibreLink Connector Service
        var libreEnabled = connectors.GetValue<bool>("LibreLinkUp:Enabled", false);
        if (libreEnabled)
        {
            var libreUsername = builder.AddParameter(
                "librelinkup-username",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:Username"] ?? "",
                secret: true
            );
            var librePassword = builder.AddParameter(
                "librelinkup-password",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:Password"] ?? "",
                secret: true
            );
            var libreRegion = builder.AddParameter(
                "librelinkup-region",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:Region"] ?? "",
                secret: false
            );
            var librePatientId = builder.AddParameter(
                "librelinkup-patient-id",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:PatientId"] ?? "",
                secret: false
            );
            var libreServer = builder.AddParameter(
                "librelinkup-server",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:Server"] ?? "",
                secret: false
            );
            var libreSyncInterval = builder.AddParameter(
                "librelinkup-sync-interval",
                value: builder.Configuration[
                    "Parameters:Connectors:LibreLinkUp:SyncIntervalMinutes"
                ] ?? "",
                secret: false
            );

            var libre = builder
                .AddProject<Projects.Nocturne_Connectors_FreeStyle>(ServiceNames.LibreConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibreUsername",
                    libreUsername
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibrePassword",
                    librePassword
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibreRegion",
                    libreRegion
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibrePatientId",
                    librePatientId
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibreServer",
                    libreServer
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}SyncIntervalMinutes",
                    libreSyncInterval
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}ConnectSource",
                    ConnectSource.LibreLinkUp.ToString()
                )
                .WithEnvironment("NocturneApiUrl", api.GetEndpoint("http"))
                .WithEnvironment("ApiSecret", apiSecret)
                .WaitFor(api)
                .WithReference(api);
        }

        // MiniMed CareLink Connector Service
        var carelinkEnabled = connectors.GetValue<bool>("CareLink:Enabled", false);
        if (carelinkEnabled)
        {
            var carelinkUsername = builder.AddParameter(
                "carelink-username",
                value: builder.Configuration["Parameters:Connectors:CareLink:Username"] ?? "",
                secret: true
            );
            var carelinkPassword = builder.AddParameter(
                "carelink-password",
                value: builder.Configuration["Parameters:Connectors:CareLink:Password"] ?? "",
                secret: true
            );
            var carelinkRegion = builder.AddParameter(
                "carelink-region",
                value: builder.Configuration["Parameters:Connectors:CareLink:Region"] ?? "",
                secret: false
            );
            var carelinkCountryCode = builder.AddParameter(
                "carelink-country-code",
                value: builder.Configuration["Parameters:Connectors:CareLink:CountryCode"] ?? "",
                secret: false
            );
            var carelinkPatientUsername = builder.AddParameter(
                "carelink-patient-username",
                value: builder.Configuration["Parameters:Connectors:CareLink:PatientUsername"]
                    ?? "",
                secret: false
            );
            var carelinkSyncInterval = builder.AddParameter(
                "carelink-sync-interval",
                value: builder.Configuration["Parameters:Connectors:CareLink:SyncIntervalMinutes"]
                    ?? "",
                secret: false
            );

            var carelink = builder
                .AddProject<Projects.Nocturne_Connectors_MiniMed>(ServiceNames.MiniMedConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkUsername",
                    carelinkUsername
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkPassword",
                    carelinkPassword
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkRegion",
                    carelinkRegion
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkCountryCode",
                    carelinkCountryCode
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkPatientUsername",
                    carelinkPatientUsername
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}SyncIntervalMinutes",
                    carelinkSyncInterval
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}ConnectSource",
                    ConnectSource.CareLink.ToString()
                )
                .WithEnvironment("NocturneApiUrl", api.GetEndpoint("http"))
                .WithEnvironment("ApiSecret", apiSecret)
                .WaitFor(api)
                .WithReference(api);
        }

        // Nightscout Connector Service
        var nightscoutConnectorEnabled = connectors.GetValue<bool>("Nightscout:Enabled", false);
        if (nightscoutConnectorEnabled)
        {
            var nightscoutSourceEndpoint = builder
                .AddParameter(
                    "nightscout-source-endpoint",
                    value: builder.Configuration["Parameters:Connectors:Nightscout:SourceEndpoint"]
                        ?? "",
                    secret: false
                )
                .WithDescription(
                    "URL of external Nightscout instance to sync data FROM (e.g., https://other-nightscout.herokuapp.com)"
                );
            var nightscoutSourceApiSecret = builder
                .AddParameter(
                    "nightscout-source-api-secret",
                    value: builder.Configuration["Parameters:Connectors:Nightscout:SourceApiSecret"]
                        ?? "",
                    secret: true
                )
                .WithDescription("API secret for the external Nightscout instance to sync FROM");
            var nightscoutSyncInterval = builder.AddParameter(
                "nightscout-sync-interval",
                value: builder.Configuration["Parameters:Connectors:Nightscout:SyncIntervalMinutes"]
                    ?? "",
                secret: false
            );

            var nightscoutConnector = builder
                .AddProject<Projects.Nocturne_Connectors_Nightscout>(
                    ServiceNames.NightscoutConnector
                )
                .WithExternalHttpEndpoints()
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}SourceEndpoint",
                    nightscoutSourceEndpoint
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}SourceApiSecret",
                    nightscoutSourceApiSecret
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}SyncIntervalMinutes",
                    nightscoutSyncInterval
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}ConnectSource",
                    ConnectSource.Nightscout.ToString()
                )
                .WithEnvironment("NocturneApiUrl", api.GetEndpoint("http"))
                .WithEnvironment("ApiSecret", apiSecret)
                .WaitFor(api)
                .WithReference(api);
        }

        // MyFitnessPal Connector Service
        var myFitnessPalEnabled = connectors.GetValue<bool>("MyFitnessPal:Enabled", false);
        if (myFitnessPalEnabled)
        {
            var myFitnessPalUsername = builder.AddParameter(
                "myfitnesspal-username",
                value: builder.Configuration["Parameters:Connectors:MyFitnessPal:Username"] ?? "",
                secret: true
            );
            var myFitnessPalPassword = builder.AddParameter(
                "myfitnesspal-password",
                value: builder.Configuration["Parameters:Connectors:MyFitnessPal:Password"] ?? "",
                secret: true
            );
            var myFitnessPalSyncDays = builder.AddParameter(
                "myfitnesspal-sync-days",
                value: builder.Configuration["Parameters:Connectors:MyFitnessPal:SyncDays"] ?? "",
                secret: false
            );
            var myFitnessPalSyncInterval = builder.AddParameter(
                "myfitnesspal-sync-interval",
                value: builder.Configuration[
                    "Parameters:Connectors:MyFitnessPal:SyncIntervalMinutes"
                ] ?? "",
                secret: false
            );

            var myFitnessPal = builder
                .AddProject<Projects.Nocturne_Connectors_MyFitnessPal>(
                    ServiceNames.MyFitnessPalConnector
                )
                .WithExternalHttpEndpoints()
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}MyFitnessPalUsername",
                    myFitnessPalUsername
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}MyFitnessPalPassword",
                    myFitnessPalPassword
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}SyncDays",
                    myFitnessPalSyncDays
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}SyncIntervalMinutes",
                    myFitnessPalSyncInterval
                )
                .WithEnvironment(
                    $"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}ConnectSource",
                    ConnectSource.MyFitnessPal.ToString()
                )
                .WithEnvironment("NocturneApiUrl", api.GetEndpoint("http"))
                .WithEnvironment("ApiSecret", apiSecret)
                .WaitFor(api)
                .WithReference(api);
        }

        // Compatibility Proxy parameters (for "try before you buy" migration testing)
        var compatProxyEnabled = builder.Configuration.GetValue<bool>(
            "Parameters:CompatibilityProxy:Enabled",
            false
        );
        if (compatProxyEnabled)
        {
            var compatProxyNightscoutUrl = builder
                .AddParameter(
                    "compat-proxy-nightscout-url",
                    value: builder.Configuration["Parameters:CompatibilityProxy:NightscoutUrl"]
                        ?? "",
                    secret: false
                )
                .WithDescription(
                    "URL of your existing/production Nightscout instance to forward writes TO during migration testing (e.g., https://my-nightscout.herokuapp.com)"
                );
            var compatProxyNightscoutSecret = builder
                .AddParameter(
                    "compat-proxy-nightscout-secret",
                    value: builder.Configuration[
                        "Parameters:CompatibilityProxy:NightscoutApiSecret"
                    ] ?? "",
                    secret: true
                )
                .WithDescription("API secret for your existing/production Nightscout instance");

            // Note: CompatibilityProxy configuration is passed through appsettings, not as individual parameters
            // The parameters above are defined for visibility in Aspire dashboard and secret management
        }


        // Add SignalR Hub URL parameter for the web app's integrated WebSocket bridge
        var signalrHubUrl = builder.AddParameter("signalr-hub-url", secret: false);

        // Add the SvelteKit web application (with integrated WebSocket bridge)
        var webPackagePath = Path.Combine(solutionRoot, "src", "Web", "packages", "app");

        var web = builder
            .AddViteApp(ServiceNames.NocturneWeb, webPackagePath, packageManager: "pnpm")
            .WithExternalHttpEndpoints()
            .WaitFor(api)
            .WithReference(api)
            .WithEnvironment("NOCTURNE_API_URL", api.GetEndpoint("http"))
            .WithEnvironment(ServiceNames.ConfigKeys.ApiSecret, apiSecret)
            .WithEnvironment("SIGNALR_HUB_URL", signalrHubUrl)
            .WithEnvironment(
                "PUBLIC_WEBSOCKET_RECONNECT_ATTEMPTS",
                builder.Configuration["WebSocket:ReconnectAttempts"] ?? "5"
            )
            .WithEnvironment(
                "PUBLIC_WEBSOCKET_MAX_RECONNECT_DELAY",
                builder.Configuration["WebSocket:MaxReconnectDelay"] ?? "30000"
            )
            .WithEnvironment(
                "PUBLIC_WEBSOCKET_RECONNECT_DELAY",
                builder.Configuration["WebSocket:ReconnectDelay"] ?? "1000"
            )
            .WithEnvironment(
                "PUBLIC_WEBSOCKET_PING_TIMEOUT",
                builder.Configuration["WebSocket:PingTimeout"] ?? "15000"
            )
            .WithEnvironment(
                "PUBLIC_WEBSOCKET_PING_INTERVAL",
                builder.Configuration["WebSocket:PingInterval"] ?? "20000"
            );

        apiSecret.WithParentRelationship(web);
        signalrHubUrl.WithParentRelationship(web);

        // Add conditional notification services (if configured in appsettings.json)
        // Note: Actual notification service projects would be added here when they exist

        // Add conditional OpenTelemetry services (if configured in appsettings.json)
        // Note: OTEL collector or Jaeger could be added here
        // builder.AddContainer("jaeger", "jaegertracing/all-in-one")
        //     .WithEndpoint(16686, targetPort: 16686, name: "jaeger-ui")
        //     .WithEndpoint(14268, targetPort: 14268, name: "jaeger-collector");

        var app = builder.Build();

        app.Run();
    }
}
