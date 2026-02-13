#pragma warning disable ASPIREPIPELINES003 // Experimental container image APIs

using Aspire.Hosting;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Nocturne.Aspire.Hosting;
using Nocturne.Core.Constants;
using Scalar.Aspire;

class Program
{
    static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        // Export developer certificate for Vite HTTPS (runs before resources start)
        builder.AddDeveloperCertificateExport();

        // Add Docker Compose publishing support
        // This enables 'aspire publish' to generate docker-compose.yml files
        // Using GitHub Container Registry for nightscout/nocturne
        var includeDashboard = builder.Configuration.GetValue<bool>(
            "Parameters:OptionalServices:AspireDashboard:Enabled",
            true
        );
        var compose = builder.AddDockerComposeEnvironment("compose");
        if (!includeDashboard)
        {
            compose.WithDashboard(enabled: false);
        }

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
        // Load publish-specific configuration (used during aspire publish)
        builder.Configuration.AddJsonFile(
            Path.Combine(solutionRoot, "appsettings.publish.json"),
            optional: true,
            reloadOnChange: false
        );

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
            postgres.PublishAsDockerComposeService((_, _) => { });

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

        // Build the oref Rust library to WebAssembly (optional - pre-built WASM is downloaded by default)
        // Set Parameters:Oref:CompileFromSource to true to compile from source (requires Rust toolchain)
        var compileOrefFromSource = builder.Configuration.GetValue<bool>(
            "Parameters:Oref:CompileFromSource",
            false
        );

        IResourceBuilder<ExecutableResource>? oref = null;
        if (compileOrefFromSource)
        {
            var orefPath = Path.Combine(solutionRoot, "src", "Core", "oref");
            Console.WriteLine("[Aspire] Compiling oref Rust/WASM from source...");

            oref = builder
                .AddExecutable(
                    "oref-wasm-build",
                    "cargo",
                    orefPath,
                    "build",
                    "--lib",
                    "--release",
                    "--features",
                    "wasm",
                    "--target",
                    "wasm32-unknown-unknown"
                )
                .ExcludeFromManifest();
        }
        else
        {
            Console.WriteLine("[Aspire] Using pre-built oref WASM (downloaded during build)");
        }

        // Add the Nocturne API service (without embedded connectors)
        // Aspire will auto-generate a Dockerfile during publish
        // Note: API runs on port 1613 internally, accessed via Vite proxy on port 1612
#pragma warning disable ASPIRECERTIFICATES001
        var api = builder
            .AddProject<Projects.Nocturne_API>(ServiceNames.NocturneApi, launchProfileName: null)
            .WithHttpsEndpoint(port: 1613, name: "api")
            .WithHttpsDeveloperCertificate()
            .PublishAsDockerComposeService((_, _) => { })
            .WithContainerBuildOptions(options =>
            {
                options.TargetPlatform =
                    ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
            })
            .WithRemoteImageName("ghcr.io/nightscout/nocturne/api")
            .WithRemoteImageTag("latest");
#pragma warning restore ASPIRECERTIFICATES001

        // If compiling oref from source, wait for it and set parent relationship
        if (oref != null)
        {
            api.WaitFor(oref);
            oref.WithParentRelationship(api);
        }
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

        // Connectors now run inside the API and need the api-secret for secret encryption
        api.WithEnvironment(ServiceNames.ConfigKeys.ApiSecret, apiSecret);

        // Connectors now run inside the API (single executable)

        // Add Demo Data Service (optional, for demonstrations and testing)
        // Uses shared extension from Nocturne.Aspire.Hosting
        var demoService = builder.AddDemoService<Projects.Nocturne_Services_Demo>(
            api,
            managedDatabase ?? remoteDatabase,
            options => options.Port = 1614
        );

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

        var bridgePackagePath = Path.Combine(solutionRoot, "src", "Web", "packages", "bridge");
        var bridge = builder.AddPnpmApp(
            "nocturne-bridge-build",
            bridgePackagePath,
            scriptName: "build"
        );

        // Add the SvelteKit web application (with integrated WebSocket bridge)
        var webPackagePath = Path.Combine(solutionRoot, "src", "Web", "packages", "app");

#pragma warning disable ASPIRECERTIFICATES001
        var web = JavaScriptHostingExtensions
            .AddViteApp(builder, ServiceNames.NocturneWeb, webPackagePath)
            .WithPnpm()
            .WithDeveloperCertificateForVite()
            .WithHttpsEndpoint(env: "PORT", port: 1612)
            .WithHttpsDeveloperCertificate()
            .WithDeveloperCertificateTrust(true)
            .WaitFor(api)
            .WaitFor(bridge)
            .WithReference(api)
            .WithReference(bridge)
            .WithEnvironment("PUBLIC_API_URL", api.GetEndpoint("api"))
            .WithEnvironment("NOCTURNE_API_URL", api.GetEndpoint("api"))
            .WithEnvironment(ServiceNames.ConfigKeys.ApiSecret, apiSecret)
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
            )
            // Default language for the application
            .WithEnvironment(
                "PUBLIC_DEFAULT_LANGUAGE",
                builder.Configuration["Language:DefaultLanguage"] ?? "en"
            )
            // Cookie names for authentication - must match API's Oidc:Cookie configuration
            .WithEnvironment(
                "COOKIE_ACCESS_TOKEN_NAME",
                builder.Configuration["Oidc:Cookie:AccessTokenName"] ?? ".Nocturne.AccessToken"
            )
            .WithEnvironment(
                "COOKIE_REFRESH_TOKEN_NAME",
                builder.Configuration["Oidc:Cookie:RefreshTokenName"] ?? ".Nocturne.RefreshToken"
            )
            .PublishAsDockerComposeService((_, _) => { })
            .WithContainerBuildOptions(options =>
            {
                // TargetPlatform for both amd64 and arm64 architectures
                options.TargetPlatform =
                    ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
            })
            .WithRemoteImageName("ghcr.io/nightscout/nocturne/web")
            .WithRemoteImageTag("latest");

#pragma warning restore ASPIRECERTIFICATES001
        apiSecret.WithParentRelationship(web);
        bridge.WithParentRelationship(web);
        // Add Scalar API Reference for unified API documentation
        // This provides a single documentation interface for all services in the Aspire dashboard
        var includeScalar = builder.Configuration.GetValue<bool>(
            "Parameters:OptionalServices:Scalar:Enabled",
            true
        );
        if (includeScalar)
        {
            builder
                .AddScalarApiReference(options =>
                {
                    options.WithTheme(ScalarTheme.Mars);
                })
                .WithApiReference(
                    api,
                    options =>
                    {
                        options
                            .AddDocument("v1", "Nocturne API")
                            .WithOpenApiRoutePattern("/openapi/{documentName}.json");
                    }
                );
        }

        // Add Watchtower for automatic container updates (optional)
        var enableWatchtower = builder.Configuration.GetValue<bool>(
            "Parameters:OptionalServices:Watchtower:Enabled",
            false
        );
        if (enableWatchtower)
        {
            Console.WriteLine("[Aspire] Adding Watchtower for automatic container updates");
            builder
                .AddContainer("watchtower", "ghcr.io/nicholas-fedor/watchtower", "latest")
                .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock")
                .WithEnvironment("WATCHTOWER_CLEANUP", "true")
                .WithEnvironment("WATCHTOWER_POLL_INTERVAL", "86400")
                .WithEnvironment("WATCHTOWER_INCLUDE_STOPPED", "false")
                .WithEnvironment("WATCHTOWER_REVIVE_STOPPED", "false")
                .PublishAsDockerComposeService((_, _) => { });
        }

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
