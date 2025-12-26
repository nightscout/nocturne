using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
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

        // Register the token provider for authentication
        // Use a singleton instance with proper HttpClient configuration
        builder.Services
            .AddHttpClient<TidepoolAuthTokenProvider>()
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri(tidepoolConfig.TidepoolServer);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            });
        builder.Services.AddSingleton(sp =>
        {
            // Create instance via HttpClientFactory to get properly configured HttpClient
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(TidepoolAuthTokenProvider));
            var config = sp.GetRequiredService<IOptions<TidepoolConnectorConfiguration>>();
            var logger = sp.GetRequiredService<ILogger<TidepoolAuthTokenProvider>>();
            var retryStrategy = sp.GetRequiredService<IRetryDelayStrategy>();
            return new TidepoolAuthTokenProvider(config, httpClient, logger, retryStrategy);
        });



        // Configure API data submitter for HTTP-based data submission
        builder.Services.AddConnectorApiDataSubmitter(builder.Configuration);

        builder.Services.AddHostedService<TidepoolHostedService>();

        // Add health checks
        // Add base connector services (State, Metrics, Strategies)
        builder.Services.AddBaseConnectorServices();
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("tidepool");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure standard connector endpoints (Sync, Capabilities, Health/Data)
        app.MapConnectorEndpoints<TidepoolConnectorService, TidepoolConnectorConfiguration>("Tidepool Connector");

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

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting Tidepool Connector Service...");

        await app.RunAsync();
    }
}
