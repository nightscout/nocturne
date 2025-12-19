using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Health;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Tidepool.Services;

namespace Nocturne.Connectors.Tidepool;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add service defaults
        builder.AddServiceDefaults();

        // Configure services
        // Bind configuration for HttpClient setup
        var tidepoolConfig = new TidepoolConnectorConfiguration();
        builder.Configuration.BindConnectorConfiguration(
            tidepoolConfig,
            "Tidepool",
            builder.Environment.ContentRootPath
        );

        // Register the fully bound configuration instance
        builder.Services.AddSingleton<IOptions<TidepoolConnectorConfiguration>>(
            new OptionsWrapper<TidepoolConnectorConfiguration>(tidepoolConfig)
        );
        builder.Services.AddSingleton(tidepoolConfig);

        // Configure typed HttpClient for TidepoolConnectorService
        builder.Services
            .AddHttpClient<TidepoolConnectorService>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri(tidepoolConfig.TidepoolServer);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });



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
        builder.Services.AddHostedService<TidepoolHostedService>();

        // Add health checks
        // Add base connector services (State, Metrics, Strategies)
        builder.Services.AddBaseConnectorServices();
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("tidepool");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure manual sync endpoint
        app.MapPost(
            "/sync",
            async (
                int? days,
                IServiceProvider serviceProvider,
                CancellationToken cancellationToken
            ) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();
                var config = serviceProvider.GetRequiredService<TidepoolConnectorConfiguration>();

                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var connectorService =
                        scope.ServiceProvider.GetRequiredService<TidepoolConnectorService>();

                    DateTime? since = days.HasValue ? DateTime.UtcNow.AddDays(-days.Value) : null;
                    logger.LogInformation(
                        "Manual sync triggered for Tidepool connector with lookback: {Days} days",
                        days
                    );

                    var success = await connectorService.SyncTidepoolDataAsync(
                        config,
                        cancellationToken,
                        since
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
                var config = serviceProvider.GetRequiredService<TidepoolConnectorConfiguration>();

                return Results.Ok(
                    new
                    {
                        service = "Tidepool Connector",
                        version = "1.0.0",
                        status = "running",
                        configuration = new
                        {
                            syncIntervalMinutes = config.SyncIntervalMinutes,
                            lastSync = DateTimeOffset.UtcNow,
                            connectSource = config.ConnectSource,
                            syncTreatments = config.SyncTreatments,
                            syncProfiles = config.SyncProfiles,
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
                var config = serviceProvider.GetRequiredService<TidepoolConnectorConfiguration>();

                if (metricsTracker == null)
                {
                    return Results.Ok(
                        new
                        {
                            connectorName = "Tidepool Connector",
                            status = "running",
                            message = "Metrics tracking not available",
                        }
                    );
                }

                var recentTimestamps = metricsTracker.GetRecentEntryTimestamps(10);

                return Results.Ok(
                    new
                    {
                        connectorName = "Tidepool Connector",
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
                            syncTreatments = config.SyncTreatments,
                            syncProfiles = config.SyncProfiles,
                        },
                    }
                );
            }
        );

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting Tidepool Connector Service...");

        await app.RunAsync();
    }
}
