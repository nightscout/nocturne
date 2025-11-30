using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Nocturne.Aspire.Host.Extensions;
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
                .AddPostgres(ServiceNames.PostgreSql + "-server")
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

        // Add connector services using extension methods
        builder.AddDexcomConnector(api, apiSecret);
        builder.AddGlookoConnector(api, apiSecret);
        builder.AddLibreLinkUpConnector(api, apiSecret);
        builder.AddMiniMedConnector(api, apiSecret);
        builder.AddNightscoutConnector(api, apiSecret);
        builder.AddMyFitnessPalConnector(api, apiSecret);

        // Add Demo Data Service (optional, for demonstrations and testing)
        var demoEnabled = builder.Configuration.GetValue<bool>(
            "Parameters:DemoMode:Enabled",
            false
        );
        IResourceBuilder<ProjectResource>? demoService = null;

        if (demoEnabled)
        {
            Console.WriteLine("[Aspire] Demo mode enabled - adding Demo Data Service");

            demoService = builder
                .AddProject<Projects.Nocturne_Services_Demo>(ServiceNames.DemoService)
                .WithHttpEndpoint(port: 0, name: "http")
                .WaitFor(
                    managedDatabase
                        ?? (IResourceBuilder<IResourceWithConnectionString>)remoteDatabase!
                );

            // Configure database connection for demo service
            if (managedDatabase != null)
            {
                demoService.WithReference(managedDatabase);
            }
            else if (remoteDatabase != null)
            {
                demoService.WithReference(remoteDatabase);
            }

            // Pass demo configuration
            demoService
                .WithEnvironment("DemoMode__Enabled", "true")
                .WithEnvironment(
                    "DemoMode__ClearOnStartup",
                    builder.Configuration["Parameters:DemoMode:ClearOnStartup"] ?? "true"
                )
                .WithEnvironment(
                    "DemoMode__RegenerateOnStartup",
                    builder.Configuration["Parameters:DemoMode:RegenerateOnStartup"] ?? "true"
                )
                .WithEnvironment(
                    "DemoMode__HistoryDays",
                    builder.Configuration["Parameters:DemoMode:HistoryDays"] ?? "90"
                )
                .WithEnvironment(
                    "DemoMode__IntervalMinutes",
                    builder.Configuration["Parameters:DemoMode:IntervalMinutes"] ?? "5"
                );

            // API should reference demo service for health monitoring
            api.WithEnvironment("DemoService__Url", demoService.GetEndpoint("http"))
                .WithEnvironment("DemoService__Enabled", "true");
        }
        else
        {
            // Tell API that demo mode is disabled
            api.WithEnvironment("DemoService__Enabled", "false");
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

        // Build the bridge package synchronously to ensure artifacts exist
        // Only build in development mode; in publish mode, assume it's already built by the CI/CD pipeline
        // @TODO in future it would be better to have Aspire handle building multi-project repos more elegantly
        if (!builder.ExecutionContext.IsPublishMode)
        {
            var bridgePackagePath = Path.Combine(solutionRoot, "src", "Web", "packages", "bridge");
            Console.WriteLine("[Aspire] Building @nocturne/bridge...");

            // On Windows, pnpm is a .cmd file that requires shell execution
            // Use cmd.exe /c to properly resolve the command
            var isWindows = OperatingSystem.IsWindows();
            var buildProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = isWindows ? "cmd.exe" : "pnpm",
                    Arguments = isWindows ? "/c pnpm run build" : "run build",
                    WorkingDirectory = bridgePackagePath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            buildProcess.Start();

            // Read output streams asynchronously to avoid deadlock
            // If the process output buffer fills up before WaitForExit() returns, it will block forever
            var stdoutTask = buildProcess.StandardOutput.ReadToEndAsync();
            var stderrTask = buildProcess.StandardError.ReadToEndAsync();

            await buildProcess.WaitForExitAsync();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (buildProcess.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to build @nocturne/bridge: {stderr}");
            }

            Console.WriteLine("[Aspire] @nocturne/bridge built successfully");
        }
        else
        {
            Console.WriteLine(
                "[Aspire] Skipping @nocturne/bridge build (publish mode - assuming pre-built)"
            );
        }

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
