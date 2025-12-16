using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Health;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Dexcom.Services;

namespace Nocturne.Connectors.Dexcom;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add service defaults
        builder.AddServiceDefaults();

        // Add connector state service
        // Add base connector services
        builder.Services.AddBaseConnectorServices();

        // Configure services
        // Bind configuration for HttpClient setup
        var dexcomConfig = new DexcomConnectorConfiguration();
        builder.Configuration.BindConnectorConfiguration(
            dexcomConfig,
            "Dexcom",
            builder.Environment.ContentRootPath
        );

        // Register the fully bound configuration instance
        builder.Services.AddSingleton<IOptions<DexcomConnectorConfiguration>>(
            new OptionsWrapper<DexcomConnectorConfiguration>(dexcomConfig)
        );

        // Configure typed HttpClient for DexcomConnectorService
        string serverUrl;
        if (dexcomConfig.DexcomServer.Equals("US", StringComparison.OrdinalIgnoreCase))
            serverUrl = "share2.dexcom.com";
        else if (
            dexcomConfig.DexcomServer.Equals("EU", StringComparison.OrdinalIgnoreCase)
            || dexcomConfig.DexcomServer.Equals("ous", StringComparison.OrdinalIgnoreCase)
        )
            serverUrl = "shareous1.dexcom.com";
        else
            serverUrl = dexcomConfig.DexcomServer;

        builder.Services.AddHttpClient<DexcomConnectorService>().ConfigureDexcomClient(serverUrl);

        // Register metrics tracker


        // Configure API data submitter for HTTP-based data submission
        var apiUrl = builder.Configuration["NocturneApiUrl"];
        var apiSecret = builder.Configuration["ApiSecret"];

        builder.Services.AddSingleton<IApiDataSubmitter>(sp =>
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = httpClientFactory.CreateClient("NocturneApi");
            var logger = sp.GetRequiredService<ILogger<ApiDataSubmitter>>();
            if (string.IsNullOrEmpty(apiUrl))
            {
                throw new InvalidOperationException("NocturneApiUrl configuration is missing.");
            }
            return new ApiDataSubmitter(httpClient, apiUrl, apiSecret, logger);
        });
        builder.Services.AddHostedService<DexcomHostedService>();

        // Add health checks
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("dexcom");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure manual sync endpoint
        app.MapPost(
            "/sync",
            async (IServiceProvider serviceProvider, CancellationToken cancellationToken) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                var config = serviceProvider.GetRequiredService<DexcomConnectorConfiguration>();

                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var connectorService =
                        scope.ServiceProvider.GetRequiredService<DexcomConnectorService>();

                    logger.LogInformation("Manual sync triggered for Dexcom connector");
                    var success = await connectorService.SyncDexcomDataAsync(
                        config,
                        cancellationToken
                    );

                    return Results.Ok(
                        new
                        {
                            success,
                            message = success ? "Sync completed successfully" : "Sync failed",
                        }
                    );
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error during manual sync");
                    return Results.Problem("Sync failed with error: " + ex.Message);
                }
            }
        );

        // Configure metrics endpoint
        app.MapGet(
            "/metrics",
            (IServiceProvider serviceProvider) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                var config = serviceProvider.GetRequiredService<DexcomConnectorConfiguration>();

                return Results.Ok(
                    new
                    {
                        service = "Dexcom Connector",
                        version = "1.0.0",
                        status = "running",
                        configuration = new
                        {
                            syncIntervalMinutes = config.SyncIntervalMinutes,
                            lastSync = DateTimeOffset.UtcNow, // In real implementation, this should be tracked
                            connectSource = config.ConnectSource,
                        },
                    }
                );
            }
        );

        // Configure health data endpoint
        app.MapGet(
            "/health/data",
            (IServiceProvider serviceProvider) =>
            {
                var metricsTracker = serviceProvider.GetService<IConnectorMetricsTracker>();
                var config = serviceProvider.GetRequiredService<DexcomConnectorConfiguration>();

                if (metricsTracker == null)
                {
                    return Results.Ok(
                        new
                        {
                            connectorName = "Dexcom Connector",
                            status = "running",
                            message = "Metrics tracking not available",
                        }
                    );
                }

                var recentTimestamps = metricsTracker.GetRecentEntryTimestamps(10);

                return Results.Ok(
                    new
                    {
                        connectorName = "Dexcom Connector",
                        status = "running",
                        metrics = new
                        {
                            totalEntries = metricsTracker.TotalEntries,
                            lastEntryTime = metricsTracker.LastEntryTime,
                            entriesLast24Hours = metricsTracker.EntriesLast24Hours,
                            lastSyncTime = metricsTracker.LastSyncTime,
                        },
                        recentEntries = recentTimestamps.Select(t => new { timestamp = t }).ToArray(),
                        configuration = new
                        {
                            syncIntervalMinutes = config.SyncIntervalMinutes,
                            connectSource = config.ConnectSource,
                        },
                    }
                );
            }
        );

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting Dexcom Connector Service...");

        await app.RunAsync();
    }
}
