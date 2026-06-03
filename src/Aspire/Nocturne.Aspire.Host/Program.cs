#pragma warning disable ASPIREPIPELINES003 // Experimental container image APIs

using Aspire.Hosting;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Yarp;
using Aspire.Hosting.Yarp.Transforms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Nocturne.Aspire.Host;
using Nocturne.Aspire.Host.Publishing;
using Nocturne.Aspire.Hosting;
using Nocturne.Aspire.Scalar;
using Nocturne.Core.Constants;
using Yarp.ReverseProxy.Transforms;

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
            "Aspire:OptionalServices:AspireDashboard:Enabled",
            true
        );
        var enableWatchtower = builder.Configuration.GetValue(
            "Aspire:OptionalServices:Watchtower:Enabled",
            false
        );

        var compose = builder.AddDockerComposeEnvironment("compose");
        if (!includeDashboard)
        {
            compose.WithDashboard(enabled: false);
        }

        // ------------------------------------------------------------------
        // PostgreSQL: managed local container vs external/remote DB.
        // ------------------------------------------------------------------
        var useRemoteDb = builder.Configuration.GetValue("PostgreSql:UseRemoteDatabase", false);

        // Path from apphost out to the repository root. Computed early because
        // the Postgres container bind-mounts canonical init scripts from it,
        // and the web block below also needs it.
        var solutionRoot = Path.GetFullPath(
            Path.Combine(builder.AppHostDirectory, "..", "..", "..")
        );

        var persistence = WorktreeDetection.DetectPersistence(solutionRoot);
        Console.WriteLine($"[Nocturne.Aspire] Postgres persistence mode: {persistence}");

        IResourceBuilder<PostgresServerResource>? postgresServer = null;
        IResourceBuilder<PostgresDatabaseResource>? managedDatabase = null;
        IResourceBuilder<ParameterResource>? postgresAppPassword = null;
        IResourceBuilder<ParameterResource>? postgresMigratorPassword = null;
        IResourceBuilder<ParameterResource>? postgresWebPassword = null;
        string? remoteAppConnectionString = null;
        string? remoteMigratorConnectionString = null;
        string? remoteWebUri = null;
        var dbName =
            builder.Configuration["Parameters:postgres-database"]
            ?? ServiceNames.Defaults.PostgresDatabase;

        if (!useRemoteDb)
        {
            // AddParameter resolves "Parameters:postgres-username" from config
            // (or env var Parameters__postgres-username) automatically.
            var postgresUsername = builder.AddParameter(
                ServiceNames.Parameters.PostgresUsername,
                ServiceNames.Defaults.PostgresUsername,
                secret: false
            ).WithPublishMetadata(
                "PostgreSQL bootstrap username",
                defaultValue: ServiceNames.Defaults.PostgresUsername);
            var postgresPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresPassword,
                secret: true
            ).WithPublishMetadata(
                "PostgreSQL bootstrap password",
                "Used only at first container start to create runtime roles");

            // Non-bootstrap role passwords. The Postgres container's init
            // script reads them via env vars and creates nocturne_migrator
            // and nocturne_app at first container start.
            postgresMigratorPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresMigratorPassword,
                secret: true
            ).WithPublishMetadata(
                "Migrator role password",
                "Password for nocturne_migrator — owns the schema, runs EF migrations");
            postgresAppPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresAppPassword,
                secret: true
            ).WithPublishMetadata(
                "App role password",
                "Password for nocturne_app — runtime role, cannot bypass Row Level Security");
            postgresWebPassword = builder.AddParameter(
                ServiceNames.Parameters.PostgresWebPassword,
                secret: true
            ).WithPublishMetadata(
                "Web role password",
                "Password for nocturne_web — bot-framework state, cannot bypass Row Level Security");

            // Container init lives in docs/postgres/container-init. Only
            // 00-init.sh is mounted into /docker-entrypoint-initdb.d so the
            // Postgres image runs it on first start. The BYO superuser
            // script lives at docs/postgres/bootstrap-roles.sql and is NOT
            // mounted — it intentionally refuses to run with its placeholder
            // passwords, which would abort container startup if picked up.
            var pgInitPath = Path.Combine(solutionRoot, "docs", "postgres", "container-init");

            var postgres = builder
                .AddPostgres(ServiceNames.PostgreSql + "-server")
                .WithUserName(postgresUsername)
                .WithPassword(postgresPassword)
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

            if (persistence == PersistenceMode.Persistent)
            {
                postgres
                    .WithLifetime(ContainerLifetime.Persistent)
                    .WithDataVolume(ServiceNames.Volumes.PostgresData);
            }

            if (builder.Environment.IsDevelopment() && persistence == PersistenceMode.Persistent)
            {
                postgres.WithPgAdmin();
            }

            postgres.PublishAsDockerComposeService(
                (_, service) =>
                {
                    // Rewrite the init-scripts bind-mount source to a relative
                    // path. Without this, Aspire emits the dev-machine absolute
                    // path as a generated NOCTURNE_POSTGRES_SERVER_BINDMOUNT_0
                    // env var, which then has to be renamed in the release
                    // bundle. Hardcoding ./init in compose.yaml lets users drop
                    // 00-init.sh into ./init/ next to compose and removes the
                    // env var entirely.
                    var initVolume = service.Volumes.FirstOrDefault(v =>
                        v.Target == "/docker-entrypoint-initdb.d"
                    );
                    if (initVolume != null)
                    {
                        initVolume.Source = "./init";
                    }
                }
            );

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
                ServiceNames.PostgreSql
            );
            remoteMigratorConnectionString = builder.Configuration.GetConnectionString(
                $"{ServiceNames.PostgreSql}-migrator"
            );
            remoteWebUri = builder.Configuration.GetConnectionString(
                $"{ServiceNames.PostgreSql}-web"
            );

            if (
                string.IsNullOrWhiteSpace(remoteAppConnectionString)
                || string.IsNullOrWhiteSpace(remoteMigratorConnectionString)
                || string.IsNullOrWhiteSpace(remoteWebUri)
            )
            {
                throw new InvalidOperationException(
                    $"Remote database enabled but three connection strings must be provided: "
                        + $"'ConnectionStrings:{ServiceNames.PostgreSql}' (runtime app role), "
                        + $"'ConnectionStrings:{ServiceNames.PostgreSql}-migrator' (schema migrator role), and "
                        + $"'ConnectionStrings:{ServiceNames.PostgreSql}-web' (web bot-state role, postgresql:// URL). "
                        + "See docs/postgres/bootstrap-roles.sql to create the three roles."
                );
            }
        }

        // ------------------------------------------------------------------
        // Secret parameters. AddParameter handles dashboard prompting and
        // env var override (Parameters__name) for free.
        // ------------------------------------------------------------------
        var instanceKey = builder.AddParameter(ServiceNames.Parameters.InstanceKey, secret: true)
            .WithPublishMetadata(
                "Instance key",
                "Minimum 12 characters — used for JWT signing and service authentication");

        // Discord bot credentials. Optional — only required if Discord bot
        // features are enabled for a deployment. Empty-string defaults let
        // AppHost start without requiring users to invent values they won't
        // use.
        var discordBotToken = builder.AddParameter("discord-bot-token", "", secret: true);
        var discordPublicKey = builder.AddParameter("discord-public-key", "", secret: false);
        var discordApplicationId = builder.AddParameter(
            "discord-application-id",
            "",
            secret: false
        );
        var discordClientSecret = builder.AddParameter("discord-client-secret", "", secret: true);

        // Platform base domain — the single hostname all services derive URLs from.
        // Production should set this to e.g. "nocturne.run" via user-secrets.
        // Injected as "BaseDomain" into both the API and SvelteKit.
        var baseDomain = builder.AddParameter("base-domain", "")
            .WithPublishMetadata(
                "Base domain",
                "Root domain only, e.g. example.com (not app.example.com — subdomains are generated per tenant)");

        // Chat platform credentials. All optional — a deployment that only
        // uses Discord shouldn't need to supply Telegram/Slack/WhatsApp
        // values. Empty-string defaults let AppHost start cleanly; the
        // individual bot integrations no-op when their credentials are
        // absent.
        var telegramBotToken = builder.AddParameter("telegram-bot-token", "", secret: true);
        var telegramWebhookSecretToken = builder.AddParameter(
            "telegram-webhook-secret-token",
            "",
            secret: true
        );
        var slackBotToken = builder.AddParameter("slack-bot-token", "", secret: true);
        var slackSigningSecret = builder.AddParameter("slack-signing-secret", "", secret: true);
        var whatsappAccessToken = builder.AddParameter("whatsapp-access-token", "", secret: true);
        var whatsappVerifyToken = builder.AddParameter("whatsapp-verify-token", "", secret: true);
        var whatsappAppSecret = builder.AddParameter("whatsapp-app-secret", "", secret: true);
        var whatsappPhoneNumberId = builder.AddParameter(
            "whatsapp-phone-number-id",
            "",
            secret: false
        );

        // ------------------------------------------------------------------
        // Nocturne API
        // ------------------------------------------------------------------
        var api = builder
            // Run mode: no port → Aspire assigns a dynamic one. Publish mode:
            // pin the in-container listen port so the generated compose bakes a
            // concrete http://nocturne-api:8080 (mirrors the web service's fixed
            // internal port) instead of an empty NOCTURNE_API_PORT placeholder.
            // This port is never host-published — YARP is the only entry point.
            .AddProject<Projects.Nocturne_API>(ServiceNames.NocturneApi, launchProfileName: null)
            .WithHttpEndpoint(
                name: "http",
                targetPort: builder.ExecutionContext.IsPublishMode ? 8080 : null)
            .PublishAsDockerComposeService((_, _) => { })
            .WithRemoteImageName("ghcr.io/nightscout/nocturne/nocturne-api")
            .WithRemoteImageTag("latest")
            .WithPublishImageMetadata(
                imageLabel: "API image",
                imageDefault: "ghcr.io/nightscout/nocturne/nocturne-api:latest")
            .WithEnvironment(ServiceNames.ConfigKeys.InstanceKey, instanceKey);

        if (
            managedDatabase != null
            && postgresServer != null
            && postgresAppPassword != null
            && postgresMigratorPassword != null
        )
        {
            api.WaitFor(managedDatabase)
                .WithNocturneDatabase(
                    postgresServer,
                    dbName,
                    postgresAppPassword,
                    postgresMigratorPassword
                );
        }
        else if (remoteAppConnectionString != null && remoteMigratorConnectionString != null)
        {
            api.WithNocturneRemoteDatabase(
                remoteAppConnectionString,
                remoteMigratorConnectionString
            );
        }
        else
        {
            throw new InvalidOperationException(
                "Database configuration error: neither managed nor remote database was configured."
            );
        }

        // The API reads its own Oidc/Platform/Jwt/etc. configuration directly
        // from its own appsettings.json + user-secrets. The host no longer
        // forwards those sections.

        // ------------------------------------------------------------------
        // Dev snapshot commands (dashboard buttons for export/import/sync)
        // ------------------------------------------------------------------
        if (builder.ExecutionContext.IsRunMode && postgresServer != null)
        {
            postgresServer.WithDevSnapshotCommands(api);
            postgresServer.WithListTenantsCommand(api);
            postgresServer.WithCreateTenantCommand(api);
            postgresServer.WithDeleteTenantCommand(api);
        }

        // ------------------------------------------------------------------
        // Demo data service (optional)
        // ------------------------------------------------------------------
        var includeDemoService = builder.Configuration.GetValue(
            "Aspire:OptionalServices:DemoService:Enabled",
            true
        );

        if (includeDemoService)
        {
            builder.AddDemoService<Projects.Nocturne_Services_Demo>(
                api,
                managedDatabase,
                options => { }
            );
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
                .WithEnvironment("PUBLIC_API_URL", api.GetEndpoint("http"))
                .WithEnvironment("NOCTURNE_API_URL", api.GetEndpoint("http"))
                .WithEnvironment(ServiceNames.ConfigKeys.InstanceKey, instanceKey)
                .WithEnvironment("DISCORD_BOT_TOKEN", discordBotToken)
                .WithEnvironment("DISCORD_PUBLIC_KEY", discordPublicKey)
                .WithEnvironment("DISCORD_APPLICATION_ID", discordApplicationId)
                .WithEnvironment("DISCORD_CLIENT_SECRET", discordClientSecret)
                .WithEnvironment("BASE_DOMAIN", baseDomain)
                // NOTE: BOT_LINK_HMAC_SECRET is not injected — oauth-state.ts
                // reuses INSTANCE_KEY (already wired above) to sign the
                // Discord OAuth2 state parameter. See src/Web/packages/app/
                // src/lib/server/bot/oauth-state.ts.
                .WithEnvironment("TELEGRAM_BOT_TOKEN", telegramBotToken)
                .WithEnvironment("TELEGRAM_WEBHOOK_SECRET_TOKEN", telegramWebhookSecretToken)
                .WithEnvironment("SLACK_BOT_TOKEN", slackBotToken)
                .WithEnvironment("SLACK_SIGNING_SECRET", slackSigningSecret)
                .WithEnvironment("WHATSAPP_ACCESS_TOKEN", whatsappAccessToken)
                .WithEnvironment("WHATSAPP_VERIFY_TOKEN", whatsappVerifyToken)
                .WithEnvironment("WHATSAPP_APP_SECRET", whatsappAppSecret)
                .WithEnvironment("WHATSAPP_PHONE_NUMBER_ID", whatsappPhoneNumberId);
            // PUBLIC_DEFAULT_LANGUAGE comes from the web app's own .env.
            // OTEL_EXPORTER_OTLP_ENDPOINT is injected by Aspire automatically.
        }

        IResourceBuilder<IResourceWithEndpoints> web;

        if (builder.ExecutionContext.IsRunMode)
        {
            var bridgePackagePath = Path.Combine(solutionRoot, "src", "Web", "packages", "bridge");
            var bridge = builder.AddPnpmApp(
                "nocturne-bridge-build",
                bridgePackagePath,
                scriptName: "build"
            );

            var viteWeb = JavaScriptHostingExtensions
                .AddViteApp(builder, ServiceNames.NocturneWeb, webPackagePath)
                .WithPnpm()
                .WithHttpHealthCheck("/")
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
            var dockerWeb = builder
                .AddDockerfile(ServiceNames.NocturneWeb, webDockerContextPath)
                .WithHttpEndpoint(env: "PORT")
                .WaitFor(api)
                .PublishAsDockerComposeService((_, _) => { })
                .WithRemoteImageName("ghcr.io/nightscout/nocturne/nocturne-web")
                .WithRemoteImageTag("latest")
                .WithPublishImageMetadata(
                    imageLabel: "Web image",
                    imageDefault: "ghcr.io/nightscout/nocturne/nocturne-web:latest");

            ConfigureWebEnvironment(dockerWeb);

            // SvelteKit needs ORIGIN when running behind a reverse proxy so SSR
            // constructs URLs with the public domain instead of the container hostname.
            // Derive from BaseDomain (bare host or host:port).
            dockerWeb.WithEnvironment(
                "ORIGIN",
                ReferenceExpression.Create($"https://{baseDomain}")
            );

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

        // API needs WEB_URL to POST chat bot alert dispatches to the SvelteKit app
        api.WithEnvironment("WEB_URL", web.GetEndpoint("http"));
        api.WithEnvironment("SCALAR_CUSTOM_CSS", NocturneScalarTheme.Build(solutionRoot));

        var webEndpoints = (IResourceBuilder<IResourceWithEndpoints>)web;

        // ------------------------------------------------------------------
        // YARP Gateway — single external HTTPS endpoint fronting all services.
        // Replaces per-resource dev certs and Vite proxy config.
        // ------------------------------------------------------------------
        var isWorktree = persistence == PersistenceMode.Ephemeral;

#pragma warning disable ASPIRECERTIFICATES001
        var gateway = builder.AddYarp("gateway").WithExternalHttpEndpoints();

        var customDomain = builder.Configuration["LocalDev:Domain"];

        if (builder.ExecutionContext.IsRunMode)
        {
            if (!string.IsNullOrEmpty(customDomain))
            {
                var cert = MkcertHelper.EnsureCertificate(customDomain);
                gateway.WithHttpsCertificate(cert);
            }
            else
            {
                gateway.WithHttpsDeveloperCertificate();
            }

            if (!isWorktree)
            {
                // Custom domain → port 443 so URLs work without a port number.
                gateway.WithHttpsEndpoint(port: !string.IsNullOrEmpty(customDomain) ? 443 : 1612);
            }
        }
        else
        {
            // Publish mode: HTTP on port 8080. Most deployments sit behind a
            // reverse proxy (Caddy, nginx, Traefik) that owns port 80/443 for
            // TLS termination. Default to 8080 to avoid conflicts.
            gateway.WithHostPort(8080);
        }
#pragma warning restore ASPIRECERTIFICATES001

        // WebSocket activity timeout: YARP's default is too short for long-lived
        // Socket.IO connections. Set a generous timeout (5 min) so idle WebSocket
        // frames between Socket.IO pings (every 20s) don't cause premature
        // "transport close" disconnects.
        gateway.WithEnvironment(
            "REVERSEPROXY__CLUSTERS__cluster_nocturne-web__HTTPREQUEST__ACTIVITYTIMEOUT",
            "00:05:00"
        );

        // In dev mode, YARP is the TLS-terminating edge proxy — it must Set
        // the X-Forwarded-* headers from its own connection info. In publish
        // mode, YARP sits behind an external reverse proxy (Caddy, nginx,
        // Traefik) that already sets these headers; using Set would overwrite
        // them (e.g. replacing X-Forwarded-Proto: https with http). Off
        // preserves the upstream headers so the API sees the correct scheme.
        var xForwardedAction = builder.ExecutionContext.IsRunMode
            ? ForwardedTransformActions.Set
            : ForwardedTransformActions.Off;

        gateway
            .WaitFor(api)
            .WaitFor(web)
            .WithConfiguration(yarp =>
            {
                // OIDC callback on apex → API (must come before /api/ → web catch-all)
                yarp.AddRoute("/api/auth/oidc/{**catch-all}", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);

                // OAuth endpoints → API (must bypass SvelteKit CSRF for external clients)
                yarp.AddRoute("/api/oauth/{**catch-all}", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);

                // Dev-only admin endpoints → API (not remote functions)
                yarp.AddRoute("/api/v4/dev-only/{**catch-all}", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);

                // Platform-admin tenant-access grant → API (sets the .basedomain grant cookie on a
                // browser navigation; must come before /api/ → web catch-all)
                yarp.AddRoute("/api/auth/platform-access/{**catch-all}", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);
                yarp.AddRoute("/api/auth/platform-access", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);

                // Bot webhooks, remote functions → web
                yarp.AddRoute("/api/{**catch-all}", webEndpoints.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);

                // Bot account linking
                yarp.AddRoute("/auth/bot/{**catch-all}", webEndpoints.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);

                // API docs (Scalar UI) — served directly by the API via Scalar.AspNetCore
                yarp.AddRoute("/scalar", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);
                yarp.AddRoute("/scalar/{**catch-all}", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);
                yarp.AddRoute("/openapi/{**catch-all}", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);

                // OAuth/OIDC discovery endpoints → API
                yarp.AddRoute("/.well-known/{**catch-all}", api.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);

                // Fallback → web (includes Socket.IO websockets, HMR, all frontend routes)
                yarp.AddRoute(webEndpoints.GetEndpoint("http"))
                    .WithTransformXForwarded("X-Forwarded-", xForwardedAction);
            });

        // When a custom domain is configured, show the custom domain URL in the
        // Aspire dashboard instead of the raw localhost endpoint.
        if (builder.ExecutionContext.IsRunMode && !string.IsNullOrEmpty(customDomain))
        {
            gateway.WithUrlForEndpoint(
                "https",
                url =>
                {
                    url.DisplayText = customDomain;
                    url.Url =
                        url.Endpoint!.Port == 443
                            ? $"https://{customDomain}"
                            : $"https://{customDomain}:{url.Endpoint.Port}";
                }
            );
        }

        // Inject BaseDomain into the API and web so they can derive WebAuthn RP ID,
        // tenant URLs, bot links, etc. In run mode, derive from the gateway's live
        // HTTPS endpoint. In publish mode, use the base-domain parameter.
        // Consumers expect a bare host:port (e.g. "localhost:1612"),
        // not a full URL — they prepend the scheme themselves.
        if (!builder.ExecutionContext.IsRunMode)
        {
            // Publish mode: inject from the user-supplied parameter
            api.WithEnvironment("BASE_DOMAIN", baseDomain);
        }

        if (builder.ExecutionContext.IsRunMode)
        {
            var gatewayEndpoint = gateway.GetEndpoint("https");
            var baseDomainExpr = !string.IsNullOrEmpty(customDomain)
                ? ReferenceExpression.Create($"{customDomain}")
                : ReferenceExpression.Create(
                    $"{gatewayEndpoint.Property(EndpointProperty.Host)}:{gatewayEndpoint.Property(EndpointProperty.Port)}"
                );

            // Single source of truth for both API and web
            api.WithEnvironment("BASE_DOMAIN", baseDomainExpr);

            ((IResourceBuilder<IResourceWithEnvironment>)web).WithEnvironment(
                "BASE_DOMAIN",
                baseDomainExpr
            );

            var hmrHost = !string.IsNullOrEmpty(customDomain) ? customDomain : "localhost";
            ((IResourceBuilder<IResourceWithEnvironment>)web)
                .WithEnvironment(
                    "VITE_HMR_CLIENT_PORT",
                    gatewayEndpoint.Property(EndpointProperty.Port)
                )
                .WithEnvironment("VITE_HMR_HOST", hmrHost);

            // Show the gateway URL on the web resource in the Aspire dashboard
            // so users can click through to the app via the HTTPS gateway.
            if (!string.IsNullOrEmpty(customDomain))
            {
                web.WithUrl($"https://{customDomain}", customDomain);
            }
            else
            {
                web.WithUrl(
                    ReferenceExpression.Create(
                        $"https://{gatewayEndpoint.Property(EndpointProperty.Host)}:{gatewayEndpoint.Property(EndpointProperty.Port)}"
                    ),
                    "Gateway"
                );
            }

            // Warn if custom domain doesn't resolve
            if (!string.IsNullOrEmpty(customDomain))
            {
                var port = isWorktree ? 0 : 1612;
                MkcertHelper.WarnIfDomainUnresolvable(customDomain, port);
            }
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

        builder.AddMermaidDiagramPublisher();
        builder.AddPortainerComposePublisher();

        var app = builder.Build();
        await app.RunAsync();
    }
}
