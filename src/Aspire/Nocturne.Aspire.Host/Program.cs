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

        // Path from apphost out to the repository root. Computed early because
        // the Postgres container bind-mounts canonical init scripts from it,
        // and the web block below also needs it.
        var solutionRoot = Path.GetFullPath(
            Path.Combine(builder.AppHostDirectory, "..", "..", ".."));

        IResourceBuilder<PostgresServerResource>? postgresServer = null;
        IResourceBuilder<PostgresDatabaseResource>? managedDatabase = null;
        IResourceBuilder<ParameterResource>? postgresAppPassword = null;
        IResourceBuilder<ParameterResource>? postgresMigratorPassword = null;
        IResourceBuilder<ParameterResource>? postgresWebPassword = null;
        string? remoteAppConnectionString = null;
        string? remoteMigratorConnectionString = null;
        string? remoteWebUri = null;
        var dbName = builder.Configuration["Parameters:postgres-database"]
            ?? ServiceNames.Defaults.PostgresDatabase;

        if (!useRemoteDb)
        {
            // AddParameter resolves "Parameters:postgres-username" from config
            // (or env var Parameters__postgres-username) automatically.
            var postgresUsername = builder.AddParameter(
                ServiceNames.Parameters.PostgresUsername, secret: false);
            var postgresPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresPassword, secret: true);

            // Non-bootstrap role passwords. The Postgres container's init
            // script reads them via env vars and creates nocturne_migrator
            // and nocturne_app at first container start.
            postgresMigratorPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresMigratorPassword, secret: true);
            postgresAppPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresAppPassword, secret: true);
            postgresWebPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresWebPassword, secret: true);

            // Container init lives in docs/postgres/container-init. Only
            // 00-init.sh is mounted into /docker-entrypoint-initdb.d so the
            // Postgres image runs it on first start. The BYO superuser
            // script lives at docs/postgres/bootstrap-roles.sql and is NOT
            // mounted — it intentionally refuses to run with its placeholder
            // passwords, which would abort container startup if picked up.
            var pgInitPath = Path.Combine(solutionRoot, "docs", "postgres", "container-init");

            var postgres = builder
                .AddPostgres(ServiceNames.PostgreSql + "-server")
                .WithLifetime(ContainerLifetime.Persistent)
                .WithUserName(postgresUsername)
                .WithPassword(postgresPassword)
                .WithDataVolume(ServiceNames.Volumes.PostgresData)
                .WithBindMount(pgInitPath, "/docker-entrypoint-initdb.d", isReadOnly: true)
                // Force the Postgres image to create the Nocturne database at
                // container init, BEFORE /docker-entrypoint-initdb.d/ scripts
                // run, so 00-init.sh executes against the same database the
                // app will later connect to. Without this, POSTGRES_DB
                // defaults to POSTGRES_USER and the init script hands schema
                // ownership on the wrong database. Aspire's AddDatabase below
                // is a no-op once the database already exists.
                .WithEnvironment("POSTGRES_DB", dbName)
                .WithEnvironment("NOCTURNE_MIGRATOR_PASSWORD", postgresMigratorPassword)
                .WithEnvironment("NOCTURNE_APP_PASSWORD", postgresAppPassword)
                .WithEnvironment("NOCTURNE_WEB_PASSWORD", postgresWebPassword);

            if (builder.Environment.IsDevelopment())
            {
                postgres.WithPgAdmin();
            }

            postgres.PublishAsDockerComposeService((_, _) => { });

            managedDatabase = postgres.AddDatabase(ServiceNames.PostgreSql, dbName);
            postgresServer = postgres;
            postgresUsername.WithParentRelationship(postgres);
            postgresPassword.WithParentRelationship(postgres);
            postgresMigratorPassword.WithParentRelationship(postgres);
            postgresAppPassword.WithParentRelationship(postgres);
            postgresWebPassword.WithParentRelationship(postgres);
        }
        else
        {
            remoteAppConnectionString = builder.Configuration.GetConnectionString(
                ServiceNames.PostgreSql);
            remoteMigratorConnectionString = builder.Configuration.GetConnectionString(
                $"{ServiceNames.PostgreSql}-migrator");
            remoteWebUri = builder.Configuration.GetConnectionString(
                $"{ServiceNames.PostgreSql}-web");

            if (string.IsNullOrWhiteSpace(remoteAppConnectionString)
                || string.IsNullOrWhiteSpace(remoteMigratorConnectionString)
                || string.IsNullOrWhiteSpace(remoteWebUri))
            {
                throw new InvalidOperationException(
                    $"Remote database enabled but three connection strings must be provided: " +
                    $"'ConnectionStrings:{ServiceNames.PostgreSql}' (runtime app role), " +
                    $"'ConnectionStrings:{ServiceNames.PostgreSql}-migrator' (schema migrator role), and " +
                    $"'ConnectionStrings:{ServiceNames.PostgreSql}-web' (web bot-state role, postgresql:// URL). " +
                    "See docs/postgres/bootstrap-roles.sql to create the three roles.");
            }
        }

        // ------------------------------------------------------------------
        // Secret parameters. AddParameter handles dashboard prompting and
        // env var override (Parameters__name) for free.
        // ------------------------------------------------------------------
        var instanceKey = builder.AddParameter(
            ServiceNames.Parameters.InstanceKey, secret: true);

        // Discord bot credentials. Optional — only required if Discord bot
        // features are enabled for a deployment. Empty-string defaults let
        // AppHost start without requiring users to invent values they won't
        // use.
        var discordBotToken      = builder.AddParameter("discord-bot-token",      "", secret: true);
        var discordPublicKey     = builder.AddParameter("discord-public-key",     "", secret: false);
        var discordApplicationId = builder.AddParameter("discord-application-id", "", secret: false);
        var discordClientSecret  = builder.AddParameter("discord-client-secret",  "", secret: true);

        // Public base domain used by the bot package to build /connect and OAuth2
        // redirect URLs. Default targets the local Aspire run (https://localhost:1612).
        // Production should set this to e.g. "nocturne.run" via user-secrets.
        var publicBaseDomain = builder.AddParameter("public-base-domain", "localhost:1612");

        // Chat platform credentials. All optional — a deployment that only
        // uses Discord shouldn't need to supply Telegram/Slack/WhatsApp
        // values. Empty-string defaults let AppHost start cleanly; the
        // individual bot integrations no-op when their credentials are
        // absent.
        var telegramBotToken          = builder.AddParameter("telegram-bot-token",          "", secret: true);
        var telegramWebhookSecretToken = builder.AddParameter("telegram-webhook-secret-token", "", secret: true);
        var slackBotToken             = builder.AddParameter("slack-bot-token",             "", secret: true);
        var slackSigningSecret        = builder.AddParameter("slack-signing-secret",        "", secret: true);
        var whatsappAccessToken       = builder.AddParameter("whatsapp-access-token",       "", secret: true);
        var whatsappVerifyToken       = builder.AddParameter("whatsapp-verify-token",       "", secret: true);
        var whatsappAppSecret         = builder.AddParameter("whatsapp-app-secret",         "", secret: true);
        var whatsappPhoneNumberId     = builder.AddParameter("whatsapp-phone-number-id",    "", secret: false);

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

        if (managedDatabase != null && postgresServer != null
            && postgresAppPassword != null && postgresMigratorPassword != null)
        {
            api.WaitFor(managedDatabase)
               .WithNocturneDatabase(postgresServer, dbName, postgresAppPassword, postgresMigratorPassword);
        }
        else if (remoteAppConnectionString != null && remoteMigratorConnectionString != null)
        {
            api.WithNocturneRemoteDatabase(remoteAppConnectionString, remoteMigratorConnectionString);
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
            managedDatabase,
            options => options.Port = 1614);

        if (demoService != null)
        {
            if (managedDatabase != null && postgresServer != null
                && postgresAppPassword != null && postgresMigratorPassword != null)
            {
                demoService.WithNocturneDatabase(postgresServer, dbName, postgresAppPassword, postgresMigratorPassword);
            }
            else if (remoteAppConnectionString != null && remoteMigratorConnectionString != null)
            {
                demoService.WithNocturneRemoteDatabase(remoteAppConnectionString, remoteMigratorConnectionString);
            }
        }

        // ------------------------------------------------------------------
        // Web app (SvelteKit + integrated WebSocket bridge)
        // ------------------------------------------------------------------
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
                .WithEnvironment("DISCORD_CLIENT_SECRET",  discordClientSecret)
                .WithEnvironment("PUBLIC_BASE_DOMAIN",     publicBaseDomain)
                // NOTE: BOT_LINK_HMAC_SECRET is not injected — oauth-state.ts
                // reuses INSTANCE_KEY (already wired above) to sign the
                // Discord OAuth2 state parameter. See src/Web/packages/app/
                // src/lib/server/bot/oauth-state.ts.
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
            if (postgresServer != null && postgresWebPassword != null)
            {
                viteWeb.WithNocturneWebDatabase(postgresServer, dbName, postgresWebPassword);
            }
            else if (remoteWebUri != null)
            {
                viteWeb.WithNocturneWebRemoteDatabase(remoteWebUri);
            }
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
            if (postgresServer != null && postgresWebPassword != null)
            {
                dockerWeb.WithNocturneWebDatabase(postgresServer, dbName, postgresWebPassword);
            }
            else if (remoteWebUri != null)
            {
                dockerWeb.WithNocturneWebRemoteDatabase(remoteWebUri);
            }
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
