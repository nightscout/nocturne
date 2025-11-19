using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Add Docker Compose environment for generating docker-compose.yml files
        // builder.AddDockerComposeEnvironment("production");

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


        // Add PostgreSQL database - use remote database connection or local container
        var useRemoteDb = builder.Configuration.GetValue<bool>("UseRemoteDatabase", false);

        Console.WriteLine($"[Aspire] UseRemoteDatabase: {useRemoteDb}");
        Console.WriteLine($"[Aspire] Environment: {builder.Environment.EnvironmentName}");

        // Get remote connection string if using remote database
        string? remoteConnectionString = null;
        if (useRemoteDb)
        {
            remoteConnectionString = builder.Configuration.GetConnectionString(
                ServiceNames.PostgreSql
            );

            if (string.IsNullOrWhiteSpace(remoteConnectionString))
            {
                throw new InvalidOperationException(
                    $"Remote database enabled but connection string '{ServiceNames.PostgreSql}' not found in configuration."
                );
            }

            Console.WriteLine($"[Aspire] Using remote database: {remoteConnectionString}");
        }

        IResourceBuilder<IResourceWithConnectionString> nocturnedb;

        if (!useRemoteDb)
        {
            // Use local PostgreSQL container
            var postgresUsername = builder.AddParameter(
                ServiceNames.Parameters.PostgresUsername,
                secret: false
            );
            var postgresPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresPassword,
                secret: true
            );
            var postgresDbName = builder.AddParameter(
                ServiceNames.Parameters.PostgresDbName,
                secret: false
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

            nocturnedb = postgres.AddDatabase(
                builder.Configuration["Parameters:postgres-database"] ?? ServiceNames.Defaults.PostgresDatabase
            );
            postgresUsername.WithParentRelationship(postgres);
            postgresPassword.WithParentRelationship(postgres);
            postgresDbName.WithParentRelationship(postgres);
        }
        else
        {
            // For remote database, create a placeholder resource
            // We'll inject the connection string directly via environment variable
            // @TODO This is really ugly but we are H A C K E R M O D E atm
            nocturnedb = builder.AddConnectionString(ServiceNames.Defaults.PostgresDatabase);
        }

        // Add the Nocturne API service (without embedded connectors)
        // Aspire will auto-generate a Dockerfile during publish
        var api = builder
            .AddProject<Projects.Nocturne_API>(ServiceNames.NocturneApi)
            .WithExternalHttpEndpoints();

        // For remote database, inject connection string directly as environment variable
        if (useRemoteDb)
        {
            api.WithEnvironment(
                $"ConnectionStrings__{ServiceNames.Defaults.PostgresDatabase}",
                remoteConnectionString!
            );
        }
        else
        {
            // For local database, use WithReference which automatically injects the connection string
            api.WaitFor(nocturnedb).WithReference(nocturnedb);
        }

        // Add connector services as independent services based on Parameters configuration
        var connectors = builder.Configuration.GetSection("Parameters:Connectors");

        // Shared Nightscout configuration parameters
        var nightscoutUrl = builder.AddParameter(
            "nightscout-url",
            secret: false
        );
        var nightscoutApiSecret = builder.AddParameter(
            "nightscout-api-secret",
            secret: true
        );

        // Dexcom Connector Service
        var dexcomEnabled = connectors.GetValue<bool>("Dexcom:Enabled", false);
        if (dexcomEnabled)
        {
            var dexcomUsername = builder.AddParameter("dexcom-username",
                value: builder.Configuration["Parameters:Connectors:Dexcom:Username"], secret: true);
            var dexcomPassword = builder.AddParameter("dexcom-password",
                value: builder.Configuration["Parameters:Connectors:Dexcom:Password"], secret: true);
            var dexcomRegion = builder.AddParameter("dexcom-region",
                value: builder.Configuration["Parameters:Connectors:Dexcom:Region"], secret: false);
            var dexcomServer = builder.AddParameter("dexcom-server",
                value: builder.Configuration["Parameters:Connectors:Dexcom:Server"], secret: false);
            var dexcomSyncInterval = builder.AddParameter("dexcom-sync-interval",
                value: builder.Configuration["Parameters:Connectors:Dexcom:SyncIntervalMinutes"], secret: false);

            var dexcom = builder
                .AddProject<Projects.Nocturne_Connectors_Dexcom>(ServiceNames.DexcomConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.DexcomPrefix}DexcomUsername", dexcomUsername)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.DexcomPrefix}DexcomPassword", dexcomPassword)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.DexcomPrefix}DexcomRegion", dexcomRegion)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.DexcomPrefix}DexcomServer", dexcomServer)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.DexcomPrefix}SyncIntervalMinutes", dexcomSyncInterval)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.DexcomPrefix}NightscoutUrl", nightscoutUrl)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.DexcomPrefix}NightscoutApiSecret", nightscoutApiSecret)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.DexcomPrefix}ConnectSource", ConnectSource.Dexcom.ToString());
        }

        // Glooko Connector Service
        var glookoEnabled = connectors.GetValue<bool>("Glooko:Enabled", false);
        if (glookoEnabled)
        {
            var glookoEmail = builder.AddParameter("glooko-email",
                value: builder.Configuration["Parameters:Connectors:Glooko:Email"], secret: true);
            var glookoPassword = builder.AddParameter("glooko-password",
                value: builder.Configuration["Parameters:Connectors:Glooko:Password"], secret: true);
            var glookoServer = builder.AddParameter("glooko-server",
                value: builder.Configuration["Parameters:Connectors:Glooko:Server"], secret: false);
            var glookoTimezoneOffset = builder.AddParameter("glooko-timezone-offset",
                value: builder.Configuration["Parameters:Connectors:Glooko:TimezoneOffset"], secret: false);
            var glookoSyncInterval = builder.AddParameter("glooko-sync-interval",
                value: builder.Configuration["Parameters:Connectors:Glooko:SyncIntervalMinutes"], secret: false);

            var glooko = builder
                .AddProject<Projects.Nocturne_Connectors_Glooko>(ServiceNames.GlookoConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.GlookoPrefix}GlookoEmail", glookoEmail)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.GlookoPrefix}GlookoPassword", glookoPassword)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.GlookoPrefix}GlookoServer", glookoServer)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.GlookoPrefix}GlookoTimezoneOffset", glookoTimezoneOffset)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.GlookoPrefix}SyncIntervalMinutes", glookoSyncInterval)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.GlookoPrefix}NightscoutUrl", nightscoutUrl)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.GlookoPrefix}NightscoutApiSecret", nightscoutApiSecret)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.GlookoPrefix}ConnectSource", ConnectSource.Glooko.ToString());
        }

        // FreeStyle LibreLink Connector Service
        var libreEnabled = connectors.GetValue<bool>("LibreLinkUp:Enabled", false);
        if (libreEnabled)
        {
            var libreUsername = builder.AddParameter("librelinkup-username",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:Username"], secret: true);
            var librePassword = builder.AddParameter("librelinkup-password",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:Password"], secret: true);
            var libreRegion = builder.AddParameter("librelinkup-region",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:Region"], secret: false);
            var librePatientId = builder.AddParameter("librelinkup-patient-id",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:PatientId"], secret: false);
            var libreServer = builder.AddParameter("librelinkup-server",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:Server"], secret: false);
            var libreSyncInterval = builder.AddParameter("librelinkup-sync-interval",
                value: builder.Configuration["Parameters:Connectors:LibreLinkUp:SyncIntervalMinutes"], secret: false);

            var libre = builder
                .AddProject<Projects.Nocturne_Connectors_FreeStyle>(ServiceNames.LibreConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibreUsername", libreUsername)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibrePassword", librePassword)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibreRegion", libreRegion)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibrePatientId", librePatientId)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}LibreServer", libreServer)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}SyncIntervalMinutes", libreSyncInterval)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}NightscoutUrl", nightscoutUrl)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}NightscoutApiSecret", nightscoutApiSecret)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.FreeStylePrefix}ConnectSource", ConnectSource.LibreLinkUp.ToString());
        }

        // MiniMed CareLink Connector Service
        var carelinkEnabled = connectors.GetValue<bool>("CareLink:Enabled", false);
        if (carelinkEnabled)
        {
            var carelinkUsername = builder.AddParameter("carelink-username",
                value: builder.Configuration["Parameters:Connectors:CareLink:Username"], secret: true);
            var carelinkPassword = builder.AddParameter("carelink-password",
                value: builder.Configuration["Parameters:Connectors:CareLink:Password"], secret: true);
            var carelinkRegion = builder.AddParameter("carelink-region",
                value: builder.Configuration["Parameters:Connectors:CareLink:Region"], secret: false);
            var carelinkCountryCode = builder.AddParameter("carelink-country-code",
                value: builder.Configuration["Parameters:Connectors:CareLink:CountryCode"], secret: false);
            var carelinkPatientUsername = builder.AddParameter("carelink-patient-username",
                value: builder.Configuration["Parameters:Connectors:CareLink:PatientUsername"], secret: false);
            var carelinkSyncInterval = builder.AddParameter("carelink-sync-interval",
                value: builder.Configuration["Parameters:Connectors:CareLink:SyncIntervalMinutes"], secret: false);

            var carelink = builder
                .AddProject<Projects.Nocturne_Connectors_MiniMed>(ServiceNames.MiniMedConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkUsername", carelinkUsername)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkPassword", carelinkPassword)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkRegion", carelinkRegion)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkCountryCode", carelinkCountryCode)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}CarelinkPatientUsername", carelinkPatientUsername)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}SyncIntervalMinutes", carelinkSyncInterval)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}NightscoutUrl", nightscoutUrl)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}NightscoutApiSecret", nightscoutApiSecret)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MiniMedPrefix}ConnectSource", ConnectSource.CareLink.ToString());
        }

        // Nightscout Connector Service
        var nightscoutConnectorEnabled = connectors.GetValue<bool>("Nightscout:Enabled", false);
        if (nightscoutConnectorEnabled)
        {
            var nightscoutSourceEndpoint = builder.AddParameter("nightscout-source-endpoint",
                value: builder.Configuration["Parameters:Connectors:Nightscout:SourceEndpoint"], secret: false);
            var nightscoutSourceApiSecret = builder.AddParameter("nightscout-source-api-secret",
                value: builder.Configuration["Parameters:Connectors:Nightscout:SourceApiSecret"], secret: true);
            var nightscoutSyncInterval = builder.AddParameter("nightscout-sync-interval",
                value: builder.Configuration["Parameters:Connectors:Nightscout:SyncIntervalMinutes"], secret: false);

            var nightscoutConnector = builder
                .AddProject<Projects.Nocturne_Connectors_Nightscout>(ServiceNames.NightscoutConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}SourceEndpoint", nightscoutSourceEndpoint)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}SourceApiSecret", nightscoutSourceApiSecret)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}SyncIntervalMinutes", nightscoutSyncInterval)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}NightscoutUrl", nightscoutUrl)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}NightscoutApiSecret", nightscoutApiSecret)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.NightscoutPrefix}ConnectSource", ConnectSource.Nightscout.ToString());
        }

        // MyFitnessPal Connector Service
        var myFitnessPalEnabled = connectors.GetValue<bool>("MyFitnessPal:Enabled", false);
        if (myFitnessPalEnabled)
        {
            var myFitnessPalUsername = builder.AddParameter("myfitnesspal-username",
                value: builder.Configuration["Parameters:Connectors:MyFitnessPal:Username"], secret: true);
            var myFitnessPalPassword = builder.AddParameter("myfitnesspal-password",
                value: builder.Configuration["Parameters:Connectors:MyFitnessPal:Password"], secret: true);
            var myFitnessPalSyncDays = builder.AddParameter("myfitnesspal-sync-days",
                value: builder.Configuration["Parameters:Connectors:MyFitnessPal:SyncDays"], secret: false);
            var myFitnessPalSyncInterval = builder.AddParameter("myfitnesspal-sync-interval",
                value: builder.Configuration["Parameters:Connectors:MyFitnessPal:SyncIntervalMinutes"], secret: false);

            var myFitnessPal = builder
                .AddProject<Projects.Nocturne_Connectors_MyFitnessPal>(ServiceNames.MyFitnessPalConnector)
                .WithExternalHttpEndpoints()
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}MyFitnessPalUsername", myFitnessPalUsername)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}MyFitnessPalPassword", myFitnessPalPassword)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}SyncDays", myFitnessPalSyncDays)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}SyncIntervalMinutes", myFitnessPalSyncInterval)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}NightscoutUrl", nightscoutUrl)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}NightscoutApiSecret", nightscoutApiSecret)
                .WithEnvironment($"{ServiceNames.ConnectorEnvironment.MyFitnessPalPrefix}ConnectSource", ConnectSource.MyFitnessPal.ToString());
        }

        // Add API_SECRET parameter for authentication
        var apiSecret = builder.AddParameter(
            ServiceNames.Parameters.ApiSecret,
            secret: true
        );

        // Add SignalR Hub URL parameter for the web app's integrated WebSocket bridge
        var signalrHubUrl = builder.AddParameter(
            "signalr-hub-url",
            secret: false
        );

        // Add the SvelteKit web application (with integrated WebSocket bridge)
        // For Azure deployment, use container. For local dev, use JavaScript app with pnpm.
        IResourceBuilder<IResourceWithEndpoints> web;
        if (builder.ExecutionContext.IsPublishMode)
        {
            // Use containerized deployment for publish/Azure
            // Build from workspace root with Dockerfile in packages/app
            var webWorkspaceRoot = Path.Combine(solutionRoot, "src", "Web");
            var dockerfilePath = Path.Combine("packages", "app", "Dockerfile");

            web = builder
                .AddDockerfile(ServiceNames.NocturneWeb, webWorkspaceRoot, dockerfilePath)
                .WithHttpEndpoint(port: 5173, targetPort: 5173, name: "http", isProxied: false)
                .WithExternalHttpEndpoints()
                .WaitFor(api)
                .WithEnvironment(ServiceNames.ConfigKeys.ApiSecret, apiSecret)
                .WithEnvironment("SIGNALR_HUB_URL", signalrHubUrl)
                .WithEnvironment("PUBLIC_API_URL", api.GetEndpoint("http"));
        }
        else
        {
            // Use Vite app for local development with pnpm workspace support
            // AddViteApp automatically creates an HTTP endpoint and configures Vite-specific optimizations
            var webRootPath = Path.Combine(solutionRoot, "src", "Web");
            web = builder
                .AddViteApp(ServiceNames.NocturneWeb, webRootPath)
                .WithPnpm() // Automatically run pnpm install before dev with frozen lockfile in production
                .WithExternalHttpEndpoints()
                .WaitFor(api)
                .WithReference(api)
                .WithEnvironment(ServiceNames.ConfigKeys.ApiSecret, apiSecret)
                .WithEnvironment("SIGNALR_HUB_URL", signalrHubUrl);
        }

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
