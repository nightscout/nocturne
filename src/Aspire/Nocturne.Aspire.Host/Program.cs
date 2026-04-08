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

        // ------------------------------------------------------------------
        // Optional services (orchestration flags — not Aspire parameters).
        // Configured under "Aspire:OptionalServices" in apphost appsettings.
        // ------------------------------------------------------------------
        var includeDashboard = builder.Configuration.GetValue(
            "Aspire:OptionalServices:AspireDashboard:Enabled", true);
        var includeScalar = builder.Configuration.GetValue(
            "Aspire:OptionalServices:Scalar:Enabled", true);
        var enableWatchtower = builder.Configuration.GetValue(
            "Aspire:OptionalServices:Watchtower:Enabled", false);

        var compose = builder.AddDockerComposeEnvironment("compose");
        if (!includeDashboard)
        {
            compose.WithDashboard(enabled: false);
        }

        // ------------------------------------------------------------------
        // PostgreSQL: managed local container vs external/remote DB.
        // ------------------------------------------------------------------
        var useRemoteDb = builder.Configuration.GetValue(
            "PostgreSql:UseRemoteDatabase", false);

        IResourceBuilder<PostgresDatabaseResource>? managedDatabase = null;
        IResourceBuilder<IResourceWithConnectionString>? remoteDatabase = null;

        if (!useRemoteDb)
        {
            // AddParameter resolves "Parameters:postgres-username" from config
            // (or env var Parameters__postgres-username) automatically.
            var postgresUsername = builder.AddParameter(
                ServiceNames.Parameters.PostgresUsername, secret: false);
            var postgresPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresPassword, secret: true);

            var dbName = builder.Configuration["Parameters:postgres-database"]
                ?? ServiceNames.Defaults.PostgresDatabase;

            var postgres = builder
                .AddPostgres(ServiceNames.PostgreSql + "-server")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithUserName(postgresUsername)
                .WithPassword(postgresPassword)
                .WithDataVolume(ServiceNames.Volumes.PostgresData);

            if (builder.Environment.IsDevelopment())
            {
                postgres.WithPgAdmin();
            }

            postgres.PublishAsDockerComposeService((_, _) => { });

            managedDatabase = postgres.AddDatabase(ServiceNames.PostgreSql, dbName);
            postgresUsername.WithParentRelationship(postgres);
            postgresPassword.WithParentRelationship(postgres);
        }
        else
        {
            var remoteConnectionString = builder.Configuration.GetConnectionString(
                ServiceNames.PostgreSql);

            if (string.IsNullOrWhiteSpace(remoteConnectionString))
            {
                throw new InvalidOperationException(
                    $"Remote database enabled but connection string '{ServiceNames.PostgreSql}' " +
                    "not found in AppHost configuration (ConnectionStrings section).");
            }

            remoteDatabase = builder.AddConnectionString(ServiceNames.PostgreSql);
        }

        // ------------------------------------------------------------------
        // Secret parameters. AddParameter handles dashboard prompting and
        // env var override (Parameters__name) for free.
        // ------------------------------------------------------------------
        var instanceKey = builder.AddParameter(
            ServiceNames.Parameters.InstanceKey, secret: true);

        var discordBotToken      = builder.AddParameter("discord-bot-token",      secret: true);
        var discordPublicKey     = builder.AddParameter("discord-public-key",     secret: false);
        var discordApplicationId = builder.AddParameter("discord-application-id", secret: false);
        var telegramBotToken          = builder.AddParameter("telegram-bot-token",          secret: true);
        var telegramWebhookSecretToken = builder.AddParameter("telegram-webhook-secret-token", secret: true);
        var slackBotToken             = builder.AddParameter("slack-bot-token",             secret: true);
        var slackSigningSecret        = builder.AddParameter("slack-signing-secret",        secret: true);
        var whatsappAccessToken       = builder.AddParameter("whatsapp-access-token",       secret: true);
        var whatsappVerifyToken       = builder.AddParameter("whatsapp-verify-token",       secret: true);
        var whatsappAppSecret         = builder.AddParameter("whatsapp-app-secret",         secret: true);
        var whatsappPhoneNumberId     = builder.AddParameter("whatsapp-phone-number-id",    secret: false);

        // ------------------------------------------------------------------
        // Nocturne API
        // ------------------------------------------------------------------
#pragma warning disable ASPIRECERTIFICATES001
        var api = builder
            .AddProject<Projects.Nocturne_API>(ServiceNames.NocturneApi, launchProfileName: null)
            .WithHttpsEndpoint(port: 1613, name: "api")
            .WithHttpsDeveloperCertificate()
            .PublishAsDockerComposeService((_, _) => { })
            .WithRemoteImageName("ghcr.io/nightscout/nocturne/api")
            .WithRemoteImageTag("latest")
            .WithEnvironment(ServiceNames.ConfigKeys.InstanceKey, instanceKey);
#pragma warning restore ASPIRECERTIFICATES001

        if (managedDatabase != null)
        {
            api.WaitFor(managedDatabase).WithReference(managedDatabase);
        }
        else if (remoteDatabase != null)
        {
            api.WithReference(remoteDatabase);
        }
        else
        {
            throw new InvalidOperationException(
                "Database configuration error: neither managed nor remote database was configured.");
        }

        // The API reads its own Oidc/Platform/Jwt/etc. configuration directly
        // from its own appsettings.json + user-secrets. The host no longer
        // forwards those sections.

        // ------------------------------------------------------------------
        // Demo data service (optional)
        // ------------------------------------------------------------------
        var demoService = builder.AddDemoService<Projects.Nocturne_Services_Demo>(
            api,
            managedDatabase ?? remoteDatabase,
            options => options.Port = 1614);

        // ------------------------------------------------------------------
        // Web app (SvelteKit + integrated WebSocket bridge)
        // ------------------------------------------------------------------
        var solutionRoot = Path.GetFullPath(
            Path.Combine(builder.AppHostDirectory, "..", "..", ".."));
        var webPackagePath = Path.Combine(solutionRoot, "src", "Web", "packages", "app");
        var webDockerContextPath = Path.Combine(solutionRoot, "src", "Web");

        IResourceBuilder<T> ConfigureWebEnvironment<T>(IResourceBuilder<T> resource)
            where T : IResourceWithEnvironment, IResourceWithEndpoints
        {
            return resource
                .WithReference(api)
                .WithEnvironment("PUBLIC_API_URL",   api.GetEndpoint("api"))
                .WithEnvironment("NOCTURNE_API_URL", api.GetEndpoint("api"))
                .WithEnvironment(ServiceNames.ConfigKeys.InstanceKey, instanceKey)
                .WithEnvironment("DISCORD_BOT_TOKEN",      discordBotToken)
                .WithEnvironment("DISCORD_PUBLIC_KEY",     discordPublicKey)
                .WithEnvironment("DISCORD_APPLICATION_ID", discordApplicationId)
                .WithEnvironment("TELEGRAM_BOT_TOKEN",             telegramBotToken)
                .WithEnvironment("TELEGRAM_WEBHOOK_SECRET_TOKEN",  telegramWebhookSecretToken)
                .WithEnvironment("SLACK_BOT_TOKEN",                slackBotToken)
                .WithEnvironment("SLACK_SIGNING_SECRET",           slackSigningSecret)
                .WithEnvironment("WHATSAPP_ACCESS_TOKEN",          whatsappAccessToken)
                .WithEnvironment("WHATSAPP_VERIFY_TOKEN",          whatsappVerifyToken)
                .WithEnvironment("WHATSAPP_APP_SECRET",            whatsappAppSecret)
                .WithEnvironment("WHATSAPP_PHONE_NUMBER_ID",       whatsappPhoneNumberId);
            // PUBLIC_DEFAULT_LANGUAGE comes from the web app's own .env.
            // OTEL_EXPORTER_OTLP_ENDPOINT is injected by Aspire automatically.
        }

#pragma warning disable ASPIRECERTIFICATES001
        IResourceBuilder<IResourceWithEndpoints> web;

        if (builder.ExecutionContext.IsRunMode)
        {
            var bridgePackagePath = Path.Combine(solutionRoot, "src", "Web", "packages", "bridge");
            var bridge = builder.AddPnpmApp(
                "nocturne-bridge-build",
                bridgePackagePath,
                scriptName: "build");

            var viteWeb = JavaScriptHostingExtensions
                .AddViteApp(builder, ServiceNames.NocturneWeb, webPackagePath)
                .WithPnpm()
                .WithHttpsEndpoint(env: "PORT", port: 1612)
                .WithHttpsDeveloperCertificate()
                .WithDeveloperCertificateTrust(true)
                .WaitFor(api)
                .WaitFor(bridge)
                .WithReference(bridge);

            ConfigureWebEnvironment(viteWeb);
            bridge.WithParentRelationship(viteWeb);
            instanceKey.WithParentRelationship(viteWeb);
            web = viteWeb;
        }
        else
        {
            var dockerWeb = builder.AddDockerfile(ServiceNames.NocturneWeb, webDockerContextPath)
                .WithHttpsEndpoint(env: "PORT", port: 1612)
                .WaitFor(api)
                .PublishAsDockerComposeService((_, _) => { })
                .WithRemoteImageName("ghcr.io/nightscout/nocturne/web")
                .WithRemoteImageTag("latest");

            ConfigureWebEnvironment(dockerWeb);
            instanceKey.WithParentRelationship(dockerWeb);
            web = dockerWeb;
        }
#pragma warning restore ASPIRECERTIFICATES001

        // API needs WEB_URL to POST chat bot alert dispatches to the SvelteKit app
        api.WithEnvironment("WEB_URL", web.GetEndpoint("https"));

        // ------------------------------------------------------------------
        // Scalar API reference (optional)
        // ------------------------------------------------------------------
        if (includeScalar)
        {
            builder
                .AddScalarApiReference(options => options.WithTheme(ScalarTheme.Mars))
                .WithApiReference(api, options =>
                {
                    options
                        .AddDocument("v1", "Nocturne API")
                        .WithOpenApiRoutePattern("/openapi/{documentName}.json");
                });
        }

        // ------------------------------------------------------------------
        // Watchtower (optional)
        // ------------------------------------------------------------------
        if (enableWatchtower)
        {
            builder
                .AddContainer("watchtower", "ghcr.io/nicholas-fedor/watchtower", "latest")
                .WithBindMount("/var/run/docker.sock", "/var/run/docker.sock")
                .WithEnvironment("WATCHTOWER_CLEANUP", "true")
                .WithEnvironment("WATCHTOWER_POLL_INTERVAL", "86400")
                .WithEnvironment("WATCHTOWER_INCLUDE_STOPPED", "false")
                .WithEnvironment("WATCHTOWER_REVIVE_STOPPED", "false")
                .PublishAsDockerComposeService((_, _) => { });
        }

        var app = builder.Build();
        await app.RunAsync();
    }
}
