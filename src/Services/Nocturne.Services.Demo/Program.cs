using Microsoft.Extensions.Options;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;

namespace Nocturne.Services.Demo;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add service defaults (health checks, OpenTelemetry, etc.)
        builder.AddServiceDefaults();

        // Configure demo mode settings
        var demoModeSection = builder.Configuration.GetSection("DemoMode");
        if (!demoModeSection.Exists())
        {
            demoModeSection = builder.Configuration.GetSection("Parameters:DemoMode");
        }

        builder.Services.Configure<DemoModeConfiguration>(demoModeSection);

        // Register demo data generation service
        builder.Services.AddSingleton<IDemoDataGenerator, DemoDataGenerator>();

        // Register demo settings generator
        builder.Services.AddSingleton<DemoSettingsGenerator>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<DemoModeConfiguration>>().Value;
            return new DemoSettingsGenerator(config);
        });

        // Configure HTTP clients for API communication
        var apiUrl = builder.Configuration["DemoService:ApiUrl"] ?? "http://localhost:5000";
        var demoHost = builder.Configuration["DemoService:DemoHost"] ?? "demo.localhost";
        var instanceKey = builder.Configuration["DemoService:InstanceKey"] ?? "";
        var instanceKeyHash = !string.IsNullOrEmpty(instanceKey)
            ? Nocturne.Connectors.Core.Utilities.HashUtils.Sha1Hex(instanceKey)
            : "";

        builder.Services.AddHttpClient("DemoAdmin", client =>
        {
            client.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromMinutes(5); // Backfill can be slow
            if (!string.IsNullOrEmpty(instanceKeyHash))
                client.DefaultRequestHeaders.Add("X-Instance-Key", instanceKeyHash);
        });

        builder.Services.AddHttpClient("DemoTenant", client =>
        {
            client.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Host = demoHost;
            client.Timeout = TimeSpan.FromMinutes(5);
            if (!string.IsNullOrEmpty(instanceKeyHash))
                client.DefaultRequestHeaders.Add("X-Instance-Key", instanceKeyHash);
        });

        // Register the API client
        builder.Services.AddSingleton<DemoApiClient>();

        // Register the hosted service for continuous data generation
        builder.Services.AddHostedService<DemoDataHostedService>();

        // Add custom health check that can be controlled
        builder.Services.AddSingleton<DemoServiceHealthCheck>();
        builder
            .Services.AddHealthChecks()
            .AddCheck<DemoServiceHealthCheck>("demo-service", tags: ["live", "ready"]);

        var app = builder.Build();

        // Map default health check endpoints (includes /health, /alive, /ready)
        app.MapDefaultEndpoints();

        // Map lifecycle endpoints
        app.MapPost("/provision", async (DemoApiClient apiClient, CancellationToken ct) =>
        {
            var state = await apiClient.ProvisionAsync(ct);
            return state != null
                ? Results.Ok(state)
                : Results.Problem("Failed to provision demo tenant");
        });

        app.MapPost("/pause", (IServiceProvider sp) =>
        {
            var hostedService = GetDemoHostedService(sp);
            if (hostedService == null)
                return Results.Problem("Demo data hosted service not found");

            hostedService.Pause();
            return Results.Ok(new { state = hostedService.State.ToString(), timestamp = DateTime.UtcNow });
        });

        app.MapPost("/resume", (IServiceProvider sp) =>
        {
            var hostedService = GetDemoHostedService(sp);
            if (hostedService == null)
                return Results.Problem("Demo data hosted service not found");

            hostedService.Resume();
            return Results.Ok(new { state = hostedService.State.ToString(), timestamp = DateTime.UtcNow });
        });

        app.MapPost("/stop", (IServiceProvider sp) =>
        {
            var hostedService = GetDemoHostedService(sp);
            if (hostedService == null)
                return Results.Problem("Demo data hosted service not found");

            hostedService.Stop();
            return Results.Ok(new { state = hostedService.State.ToString(), timestamp = DateTime.UtcNow });
        });

        app.MapPost("/wipe", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var hostedService = GetDemoHostedService(sp);
            if (hostedService == null)
                return Results.Problem("Demo data hosted service not found");

            await hostedService.WipeAsync(ct);
            return Results.Ok(new { message = "Demo data wiped", timestamp = DateTime.UtcNow });
        });

        app.MapPost("/reconfigure", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var hostedService = GetDemoHostedService(sp);
            if (hostedService == null)
                return Results.Problem("Demo data hosted service not found");

            await hostedService.ReconfigureAsync(ct);
            return Results.Ok(new { message = "Demo data reconfigured", state = hostedService.State.ToString(), timestamp = DateTime.UtcNow });
        });

        app.MapPost("/regenerate", async (IServiceProvider sp, CancellationToken ct) =>
        {
            var hostedService = GetDemoHostedService(sp);
            if (hostedService == null)
                return Results.Problem("Demo data hosted service not found");

            await hostedService.RegenerateDataAsync(ct);
            return Results.Ok(new { message = "Demo data regeneration triggered", timestamp = DateTime.UtcNow });
        });

        app.MapGet("/status", (IServiceProvider sp, IDemoDataGenerator generator) =>
        {
            var hostedService = GetDemoHostedService(sp);
            return Results.Ok(new
            {
                service = "Demo Data Service",
                version = "2.0.0",
                state = hostedService?.State.ToString() ?? "Unknown",
                isGenerating = generator.IsRunning,
                configuration = generator.GetConfiguration(),
            });
        });

        // Endpoint to get UI settings configuration (demo mode data for frontend settings pages)
        app.MapGet("/ui-settings", (DemoSettingsGenerator settingsGenerator) =>
        {
            var settings = settingsGenerator.GenerateSettings();
            return Results.Ok(settings);
        });

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting Demo Data Service (HTTP-only mode)...");

        await app.RunAsync();
    }

    private static DemoDataHostedService? GetDemoHostedService(IServiceProvider sp)
    {
        return sp.GetServices<IHostedService>()
            .OfType<DemoDataHostedService>()
            .FirstOrDefault();
    }
}
