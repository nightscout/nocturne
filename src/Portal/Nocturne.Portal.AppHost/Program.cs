#pragma warning disable ASPIREPIPELINES003 // Experimental container image APIs

using Aspire.Hosting;
using Aspire.Hosting.Publishing;
using Microsoft.Extensions.Configuration;
using Nocturne.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Export developer certificate for Vite HTTPS (runs before resources start)
builder.AddDeveloperCertificateExport();

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

    // Add dedicated PostgreSQL for demo
    var demoPostgres = builder
        .AddPostgres("demo-postgres")
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume("demo-postgres-data");

    var demoDatabase = demoPostgres.AddDatabase("nocturne-postgres", "nocturne_demo");

    // Add Nocturne API in demo mode
    // Note: We pass launchProfileName: null to avoid port conflicts with the default
    // launchSettings.json ports (1612/7209) which may already be in use by other instances
    demoApi = builder
        .AddProject<Projects.Nocturne_API>("demo-api", launchProfileName: null)
        .WithReference(demoDatabase)
        .WaitFor(demoDatabase)
        .WithHttpsDeveloperCertificate()
        .WithHttpsEndpoint(name: "demo-api", port: 1622)
        .WithContainerBuildOptions(options =>
        {
            options.TargetPlatform =
                ContainerTargetPlatform.LinuxAmd64 | ContainerTargetPlatform.LinuxArm64;
        })
        .WithEnvironment("DemoService__Enabled", "true")
        // Seed demo user accounts
        // Admin account - full access
        .WithEnvironment("LocalIdentity__SeedUsers__0__Email", "admin@demo.nocturne.local")
        .WithEnvironment("LocalIdentity__SeedUsers__0__Password", "DemoAdmin123!")
        .WithEnvironment("LocalIdentity__SeedUsers__0__DisplayName", "Demo Admin")
        .WithEnvironment("LocalIdentity__SeedUsers__0__IsAdmin", "true")
        // Teacher account - caregiver access
        .WithEnvironment("LocalIdentity__SeedUsers__1__Email", "teacher@demo.nocturne.local")
        .WithEnvironment("LocalIdentity__SeedUsers__1__Password", "DemoTeacher123!")
        .WithEnvironment("LocalIdentity__SeedUsers__1__DisplayName", "Demo Teacher")
        .WithEnvironment("LocalIdentity__SeedUsers__1__Roles__0", "readable")
        .WithEnvironment("LocalIdentity__SeedUsers__1__Roles__1", "caregiver")
        // HCP (Healthcare Provider) account - caregiver access
        .WithEnvironment("LocalIdentity__SeedUsers__2__Email", "hcp@demo.nocturne.local")
        .WithEnvironment("LocalIdentity__SeedUsers__2__Password", "DemoHCP123!")
        .WithEnvironment("LocalIdentity__SeedUsers__2__DisplayName", "Demo HCP")
        .WithEnvironment("LocalIdentity__SeedUsers__2__Roles__0", "readable")
        .WithEnvironment("LocalIdentity__SeedUsers__2__Roles__1", "caregiver");

    // Add Demo Data Service
    var demoService = builder
        .AddProject<Projects.Nocturne_Services_Demo>("demo-service")
        .WithReference(demoDatabase)
        .WaitFor(demoDatabase)
        .WaitFor(demoApi)
        .WithHttpsEndpoint(name: "demo-service-https", port: 1624)
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

    demoApi.WithEnvironment("DemoService__Url", demoService.GetEndpoint("demo-service-https"));

    // Add Nocturne Web pointing to demo API
    demoWeb = builder
        .AddViteApp("demo-web", "../../Web/packages/app", packageManager: "pnpm")
        .WithPnpmPackageInstallation()
        .WithReference(demoApi)
        .WaitFor(demoApi)
        .WithEnvironment("PUBLIC_API_URL", demoApi.GetEndpoint("demo-api"))
        .WithEnvironment("NOCTURNE_API_URL", demoApi.GetEndpoint("demo-api"))
        .WithDeveloperCertificateForVite()
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
    .WithDeveloperCertificateForVite()
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
app.Run();
