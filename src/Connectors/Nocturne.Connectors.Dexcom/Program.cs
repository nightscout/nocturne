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
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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
        builder.Services.AddSingleton(dexcomConfig);

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

        // Register the token provider for authentication
        builder.Services.AddHttpClient<DexcomAuthTokenProvider>().ConfigureDexcomClient(serverUrl);
        builder.Services.AddSingleton<IAuthTokenProvider>(sp =>
            sp.GetRequiredService<DexcomAuthTokenProvider>());

        // Register metrics tracker


        // Configure API data submitter for HTTP-based data submission
        builder.Services.AddConnectorApiDataSubmitter(builder.Configuration);
        builder.Services.AddHostedService<DexcomHostedService>();

        // Add health checks
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("dexcom");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure standard connector endpoints (Sync, Capabilities, Health/Data)
        app.MapConnectorEndpoints<DexcomConnectorService, DexcomConnectorConfiguration>("Dexcom Connector");

        // Configure metrics endpoint (Dexcom specific for now)
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

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting Dexcom Connector Service...");

        await app.RunAsync();
    }
}
