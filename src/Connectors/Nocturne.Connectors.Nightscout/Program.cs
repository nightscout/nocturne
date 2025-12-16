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
using Nocturne.Connectors.Nightscout.Services;

namespace Nocturne.Connectors.Nightscout;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add service defaults
        builder.AddServiceDefaults();

        // Configure services
        // Bind configuration for HttpClient setup
        var nightscoutConfig = new NightscoutConnectorConfiguration();
        builder.Configuration.BindConnectorConfiguration(
            nightscoutConfig,
            "Nightscout",
            builder.Environment.ContentRootPath
        );

        // Register the fully bound configuration instance
        builder.Services.AddSingleton<IOptions<NightscoutConnectorConfiguration>>(
            new OptionsWrapper<NightscoutConnectorConfiguration>(nightscoutConfig)
        );

        builder
            .Services.AddHttpClient<NightscoutConnectorService>()
            .ConfigureNightscoutClient(
                nightscoutConfig.SourceEndpoint,
                nightscoutConfig.SourceApiSecret
            );



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
        builder.Services.AddHostedService<NightscoutHostedService>();

        // Add health checks
        // Add base connector services (State, Metrics, Strategies)
        builder.Services.AddBaseConnectorServices();
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("nightscout");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure manual sync endpoint
        app.MapPost(
            "/sync",
            async (IServiceProvider serviceProvider, CancellationToken cancellationToken) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                var config = serviceProvider
                    .GetRequiredService<IOptionsSnapshot<NightscoutConnectorConfiguration>>()
                    .Value;

                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var connectorService =
                        scope.ServiceProvider.GetRequiredService<NightscoutConnectorService>();

                    logger.LogInformation("Manual sync triggered for Nightscout connector");
                    var success = await connectorService.SyncNightscoutDataAsync(
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

        // Configure health data endpoint
        app.MapGet(
            "/health/data",
            (IServiceProvider serviceProvider) =>
            {
                var metricsTracker = serviceProvider.GetService<IConnectorMetricsTracker>();
                var config = serviceProvider
                    .GetRequiredService<IOptionsSnapshot<NightscoutConnectorConfiguration>>()
                    .Value;

                if (metricsTracker == null)
                {
                    return Results.Ok(
                        new
                        {
                            connectorName = "Nightscout Connector",
                            status = "running",
                            message = "Metrics tracking not available",
                        }
                    );
                }

                var recentTimestamps = metricsTracker.GetRecentEntryTimestamps(10);

                return Results.Ok(
                    new
                    {
                        connectorName = "Nightscout Connector",
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
        logger.LogInformation("Starting Nightscout Connector Service...");

        await app.RunAsync();
    }
}
