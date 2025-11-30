using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts;
using Nocturne.Infrastructure.Data.Abstractions;
using Nocturne.Infrastructure.Data.Extensions;
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

        // Configure PostgreSQL database
        var postgresConnectionString = builder.Configuration.GetConnectionString(
            ServiceNames.PostgreSql
        );

        if (string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            throw new InvalidOperationException(
                $"PostgreSQL connection string '{ServiceNames.PostgreSql}' not found. Ensure Aspire is properly configured."
            );
        }

        builder.Services.AddPostgreSqlInfrastructure(
            postgresConnectionString,
            config =>
            {
                config.EnableDetailedErrors = builder.Environment.IsDevelopment();
                config.EnableSensitiveDataLogging = builder.Environment.IsDevelopment();
            }
        );

        // Configure demo mode settings
        builder.Services.Configure<DemoModeConfiguration>(
            builder.Configuration.GetSection("DemoMode")
        );

        // Register demo data generation service
        builder.Services.AddSingleton<IDemoDataGenerator, DemoDataGenerator>();

        // Register demo data entry/treatment services
        builder.Services.AddScoped<IDemoEntryService, DemoEntryService>();
        builder.Services.AddScoped<IDemoTreatmentService, DemoTreatmentService>();

        // Register the hosted service for continuous data generation
        builder.Services.AddHostedService<DemoDataHostedService>();

        // Add custom health check that can be controlled
        builder.Services.AddSingleton<DemoServiceHealthCheck>();
        builder
            .Services.AddHealthChecks()
            .AddCheck<DemoServiceHealthCheck>("demo-service", tags: new[] { "live", "ready" });

        var app = builder.Build();

        // Map default health check endpoints (only works in Development)
        app.MapDefaultEndpoints();

        // Always expose health endpoint for monitoring (regardless of environment)
        app.MapHealthChecks("/health");

        // Map demo service control endpoints
        app.MapGet(
            "/status",
            (IDemoDataGenerator generator) =>
            {
                return Results.Ok(
                    new
                    {
                        service = "Demo Data Service",
                        version = "1.0.0",
                        status = "running",
                        isGenerating = generator.IsRunning,
                        configuration = generator.GetConfiguration(),
                    }
                );
            }
        );

        // Endpoint to get current demo data statistics
        app.MapGet(
            "/stats",
            async (IServiceProvider sp, CancellationToken ct) =>
            {
                using var scope = sp.CreateScope();
                var postgreSqlService =
                    scope.ServiceProvider.GetRequiredService<IPostgreSqlService>();

                // Count demo entries using find query
                var entriesCount = await postgreSqlService.CountEntriesAsync(
                    findQuery: "{\"is_demo\":true}",
                    cancellationToken: ct
                );

                return Results.Ok(
                    new { demoEntriesCount = entriesCount, timestamp = DateTime.UtcNow }
                );
            }
        );

        // Endpoint to manually trigger a data regeneration (clear + reseed)
        app.MapPost(
            "/regenerate",
            async (IServiceProvider sp, CancellationToken ct) =>
            {
                using var scope = sp.CreateScope();
                var hostedService = sp.GetServices<IHostedService>()
                    .OfType<DemoDataHostedService>()
                    .FirstOrDefault();

                if (hostedService == null)
                {
                    return Results.Problem("Demo data hosted service not found");
                }

                await hostedService.RegenerateDataAsync(ct);

                return Results.Ok(
                    new
                    {
                        message = "Demo data regeneration triggered",
                        timestamp = DateTime.UtcNow,
                    }
                );
            }
        );

        // Endpoint to clear all demo data
        app.MapDelete(
            "/clear",
            async (IServiceProvider sp, CancellationToken ct) =>
            {
                using var scope = sp.CreateScope();
                var postgreSqlService =
                    scope.ServiceProvider.GetRequiredService<IPostgreSqlService>();

                var entriesDeleted = await postgreSqlService.DeleteDemoEntriesAsync(ct);
                var treatmentsDeleted = await postgreSqlService.DeleteDemoTreatmentsAsync(ct);

                return Results.Ok(
                    new
                    {
                        message = "Demo data cleared",
                        entriesDeleted,
                        treatmentsDeleted,
                        timestamp = DateTime.UtcNow,
                    }
                );
            }
        );

        // Run database migrations
        try
        {
            Console.WriteLine("[Demo Service] Running PostgreSQL database migrations...");
            await app.Services.MigrateDatabaseAsync();
            Console.WriteLine(
                "[Demo Service] PostgreSQL database migrations completed successfully."
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[Demo Service] Failed to run PostgreSQL database migrations: {ex.Message}"
            );
            Console.WriteLine(
                "[Demo Service] The application will continue, but database operations may fail."
            );
        }

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting Demo Data Service...");

        await app.RunAsync();
    }
}
