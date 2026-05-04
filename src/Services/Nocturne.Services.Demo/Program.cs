using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Repositories;
using Nocturne.Core.Contracts.V4.Repositories;
using Nocturne.Infrastructure.Data.Extensions;
using Nocturne.Infrastructure.Data.Interceptors;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Services.Demo.Configuration;
using Nocturne.Services.Demo.Services;

namespace Nocturne.Services.Demo;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Suppress verbose EF Core logging to reduce memory pressure from console log accumulation
        builder.Logging.AddFilter(
            "Microsoft.EntityFrameworkCore.Database.Command",
            LogLevel.Warning
        );
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Infrastructure", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Query", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Update", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Model", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.ChangeTracking", LogLevel.Warning);

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

        // Register V4 repositories needed for demo data
        builder.Services.AddScoped<ISensorGlucoseRepository, SensorGlucoseRepository>();
        builder.Services.AddScoped<IPatientInsulinRepository, PatientInsulinRepository>();

        // Register audit context for V4 repositories (demo service uses system context)
        builder.Services.AddScoped<IAuditContext>(_ =>
            SystemAuditContext.ForService("service:demo-generator"));

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

        // Map default health check endpoints (includes /health, /alive, /ready)
        app.MapDefaultEndpoints();

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

        // Endpoint to get UI settings configuration (demo mode data for frontend settings pages)
        app.MapGet(
            "/ui-settings",
            (DemoSettingsGenerator settingsGenerator) =>
            {
                var settings = settingsGenerator.GenerateSettings();
                return Results.Ok(settings);
            }
        );

        // Endpoint to get current demo data statistics
        app.MapGet(
            "/stats",
            async (IServiceProvider sp, CancellationToken ct) =>
            {
                using var scope = sp.CreateScope();
                var sensorGlucoseRepository =
                    scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

                var entriesCount = await sensorGlucoseRepository.CountBySourceAsync(
                    DataSources.DemoService,
                    ct
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
                var sensorGlucoseRepository =
                    scope.ServiceProvider.GetRequiredService<ISensorGlucoseRepository>();

                var entriesDeleted = await sensorGlucoseRepository.DeleteBySourceAsync(
                    DataSources.DemoService,
                    ct
                );

                return Results.Ok(
                    new
                    {
                        message = "Demo data cleared",
                        entriesDeleted,
                        timestamp = DateTime.UtcNow,
                    }
                );
            }
        );

        // Run database migrations under the migrator role
        var migratorConnectionString = builder.Configuration.GetConnectionString(
            $"{ServiceNames.PostgreSql}-migrator");
        try
        {
            if (string.IsNullOrWhiteSpace(migratorConnectionString))
            {
                throw new InvalidOperationException(
                    $"ConnectionStrings:{ServiceNames.PostgreSql}-migrator is required. " +
                    "See docs/postgres/bootstrap-roles.sql.");
            }

            DatabaseInitializationExtensions.ValidateRoleSeparation(
                postgresConnectionString!, migratorConnectionString);

            Console.WriteLine("[Demo Service] Running PostgreSQL database migrations...");
            using var migrationScope = app.Services.CreateScope();
            var interceptor = migrationScope.ServiceProvider.GetRequiredService<TenantConnectionInterceptor>();
            var migrationLogger = migrationScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            await DatabaseInitializationExtensions.RunMigrationsAsync(
                migratorConnectionString, migrationLogger, interceptor);
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
