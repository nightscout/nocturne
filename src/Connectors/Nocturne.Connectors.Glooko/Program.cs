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
using Nocturne.Connectors.Glooko.Constants;
using Nocturne.Connectors.Glooko.Models;
using Nocturne.Connectors.Glooko.Services;

namespace Nocturne.Connectors.Glooko;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add service defaults
        builder.AddServiceDefaults();

        // Add connector state service
        // Add base connector services (State, Metrics, Strategies)
        builder.Services.AddBaseConnectorServices();

        // Configure services
        // Bind configuration for HttpClient setup
        var glookoConfig = new GlookoConnectorConfiguration();
        builder.Configuration.BindConnectorConfiguration(
            glookoConfig,
            "Glooko",
            builder.Environment.ContentRootPath
        );

        // Register the fully bound configuration instance
        builder.Services.AddSingleton<IOptions<GlookoConnectorConfiguration>>(
            new OptionsWrapper<GlookoConnectorConfiguration>(glookoConfig)
        );

        // Debug: Log configuration values at startup
        Console.WriteLine($"[Glooko] Configuration loaded:");
        Console.WriteLine($"  TimezoneOffset: {glookoConfig.TimezoneOffset}");
        Console.WriteLine($"  SaveRawData: {glookoConfig.SaveRawData}");
        Console.WriteLine($"  Server: {glookoConfig.GlookoServer}");
        Console.WriteLine($"  Username: {glookoConfig.GlookoUsername}");

        var server = glookoConfig.GlookoServer?.ToUpperInvariant() switch
        {
            "US" => GlookoConstants.Servers.US,
            "EU" => GlookoConstants.Servers.EU,
            _ => GlookoConstants.Configuration.DefaultServer,
        };

        builder.Services.AddHttpClient<GlookoConnectorService>().ConfigureGlookoClient(server);

        builder.Services.AddSingleton(
            typeof(IConnectorFileService<>),
            typeof(ConnectorFileService<>)
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
        builder.Services.AddHostedService<GlookoHostedService>();

        // Add health checks
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("glooko");

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
                var config = serviceProvider
                    .GetRequiredService<IOptionsSnapshot<GlookoConnectorConfiguration>>()
                    .Value;

                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var connectorService =
                        scope.ServiceProvider.GetRequiredService<GlookoConnectorService>();

                    DateTime? since = null;
                    if (days.HasValue)
                    {
                        since = DateTime.UtcNow.AddDays(-days.Value);
                        logger.LogInformation(
                            "Manual sync requested with {Days} day lookback (since {Since})",
                            days.Value,
                            since
                        );
                    }

                    logger.LogInformation("Manual sync triggered for Glooko connector");
                    var healthSuccess = await connectorService.SyncGlookoHealthDataAsync(
                        config,
                        cancellationToken,
                        since
                    );

                    var treatmentsSuccess = await connectorService.FetchAndUploadTreatmentsAsync(
                        since,
                        config
                    );

                    var success = healthSuccess && treatmentsSuccess;

                    return Results.Ok(
                        new
                        {
                            success,
                            message = success ? "Sync completed successfully" : "Sync completed with warnings",
                            details = new { healthData = healthSuccess, treatments = treatmentsSuccess }
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
                    .GetRequiredService<IOptionsSnapshot<GlookoConnectorConfiguration>>()
                    .Value;

                if (metricsTracker == null)
                {
                    return Results.Ok(
                        new
                        {
                            connectorName = "Glooko Connector",
                            status = "running",
                            message = "Metrics tracking not available",
                        }
                    );
                }

                var recentTimestamps = metricsTracker.GetRecentEntryTimestamps(10);

                return Results.Ok(
                    new
                    {
                        connectorName = "Glooko Connector",
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
        logger.LogInformation("Starting Glooko Connector Service...");

        await app.RunAsync();
    }
}
