#pragma warning disable ASPIREPIPELINES003 // Experimental container image APIs

using Aspire.Hosting;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Nocturne.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Add the Portal API service
#pragma warning disable ASPIRECERTIFICATES001
var api = builder
    .AddProject<Projects.Nocturne_Portal_API>("portal-api")
    .WithHttpsDeveloperCertificate()
    .WithHttpsEndpoint(port: 1610)
    .WithContainerBuildOptions(options =>
    {
        options.TargetPlatform =
            ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
    });

// Conditional demo instance (Nocturne API + Web with demo data)
var demoEnabled = builder.Configuration.GetValue<bool>("Parameters:DemoApi:Enabled", false);

IResourceBuilder<ProjectResource>? demoApi = null;
IResourceBuilder<ExecutableResource>? demoWeb = null;

if (demoEnabled)
{
    Console.WriteLine("[Portal] Demo API enabled - adding demo instance");

    var solutionRoot = Path.GetFullPath(
        Path.Combine(builder.AppHostDirectory, "..", "..", "..")
    );
    var pgInitPath = Path.Combine(solutionRoot, "docs", "postgres", "container-init");
    var dbName = "nocturne_demo";

    // Non-privileged role passwords. The Postgres init script reads these via
    // env vars and creates nocturne_migrator and nocturne_app at first start.
    var demoMigratorPassword = builder.AddParameter("demo-migrator-password", "demo-migrator-dev", secret: true);
    var demoAppPassword = builder.AddParameter("demo-app-password", "demo-app-dev", secret: true);
    var demoWebPassword = builder.AddParameter("demo-web-password", "demo-web-dev", secret: true);

    // Add dedicated PostgreSQL for demo with multi-role init script
    var demoPostgres = builder
        .AddPostgres("demo-postgres")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume("demo-postgres-data")
        .WithBindMount(pgInitPath, "/docker-entrypoint-initdb.d", isReadOnly: true)
        .WithEnvironment("POSTGRES_DB", dbName)
        .WithEnvironment("NOCTURNE_MIGRATOR_PASSWORD", demoMigratorPassword)
        .WithEnvironment("NOCTURNE_APP_PASSWORD", demoAppPassword)
        .WithEnvironment("NOCTURNE_WEB_PASSWORD", demoWebPassword);

    var demoDatabase = demoPostgres.AddDatabase("demo-nocturne-postgres", dbName);

    // Add Nocturne API in demo mode.
    // Note: We pass launchProfileName: null to avoid port conflicts with the default
    // launchSettings.json ports (1612/7209) which may already be in use by other instances.
    // Do NOT use WithReference(demoDatabase) — that injects the bootstrap superuser connection
    // string, bypassing the two-role RLS model. WithNocturneDatabase injects the correct
    // nocturne_app and nocturne_migrator connection strings.
    demoApi = builder
        .AddProject<Projects.Nocturne_API>("demo-api", launchProfileName: null)
        .WaitFor(demoDatabase)
        .WithNocturneDatabase(demoPostgres, dbName, demoAppPassword, demoMigratorPassword)
        .WithHttpsDeveloperCertificate()
        .WithHttpsEndpoint(name: "demo-api", port: 1622)
        .WithContainerBuildOptions(options =>
        {
            options.TargetPlatform =
                ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
        })
        .WithEnvironment("DemoService__Enabled", "true");

    // Add Demo Data Service
    var demoService = builder
        .AddProject<Projects.Nocturne_Services_Demo>("demo-service")
        .WaitFor(demoDatabase)
        .WaitFor(demoApi)
        .WithNocturneDatabase(demoPostgres, dbName, demoAppPassword, demoMigratorPassword)
        .WithHttpEndpoint(name: "demo-service-http", port: 1624)
        .WithContainerBuildOptions(options =>
        {
            options.TargetPlatform =
                ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
        })
        .WithEnvironment("DemoMode__Enabled", "true")
        .WithEnvironment("DemoMode__ClearOnStartup", "true")
        .WithEnvironment("DemoMode__RegenerateOnStartup", "true")
        .WithEnvironment("DemoMode__BackfillDays", "90")
        .WithEnvironment("DemoMode__IntervalMinutes", "5")
        .WithEnvironment("DemoMode__ResetIntervalMinutes", "20");

    demoApi.WithEnvironment("DemoService__Url", demoService.GetEndpoint("demo-service-http"));

    // Add Nocturne Web pointing to demo API
    demoWeb = builder
        .AddViteApp("demo-web", "../../Web/packages/app", packageManager: "pnpm")
        .WithPnpmPackageInstallation()
        .WithReference(demoApi)
        .WaitFor(demoApi)
        .WithEnvironment("PUBLIC_API_URL", demoApi.GetEndpoint("demo-api"))
        .WithEnvironment("NOCTURNE_API_URL", demoApi.GetEndpoint("demo-api"))
        .WithHttpsEndpoint(env: "PORT", port: 1621, name: "https")
        .WithHttpsDeveloperCertificate()
        .WithDeveloperCertificateTrust(true)
        .WithContainerBuildOptions(options =>
        {
            options.TargetPlatform =
                ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
        });
}

// Add the Portal Web frontend
var portalWeb = JavaScriptHostingExtensions
    .AddViteApp(builder, "portal-web", "../../Web/packages/portal")
    .WithPnpm()
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("VITE_PORTAL_API_URL", api.GetEndpoint("https"))
    .WithHttpsEndpoint(env: "PORT", port: 1611)
    .WithHttpsDeveloperCertificate()
    .WithDeveloperCertificateTrust(true)
    .WithContainerBuildOptions(options =>
    {
        options.TargetPlatform =
            ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
    })
    .PublishAsDockerFile();

// Pass demo URLs to portal web when demo is enabled
if (demoEnabled && demoApi != null && demoWeb != null)
{
    portalWeb
        .WithEnvironment("VITE_DEMO_ENABLED", "true")
        .WithEnvironment("VITE_DEMO_API_URL", demoApi.GetEndpoint("demo-api"))
        .WithEnvironment("VITE_DEMO_WEB_URL", demoWeb.GetEndpoint("https"));
}
else
{
    portalWeb.WithEnvironment("VITE_DEMO_ENABLED", "false");
}

#pragma warning restore ASPIRECERTIFICATES001

var app = builder.Build();
await app.RunAsync();
