using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.MyFitnessPal.Models;
using Nocturne.Connectors.MyFitnessPal.Services;

namespace Nocturne.Connectors.MyFitnessPal;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add service defaults
        builder.AddServiceDefaults();

        // Configure services
        // Bind configuration for HttpClient setup
        var mfpConfig = new MyFitnessPalConnectorConfiguration();
        builder.Configuration.BindConnectorConfiguration(
            mfpConfig,
            "MyFitnessPal",
            builder.Environment.ContentRootPath
        );

        // Register the fully bound configuration instance
        builder.Services.AddSingleton<IOptions<MyFitnessPalConnectorConfiguration>>(
            new OptionsWrapper<MyFitnessPalConnectorConfiguration>(mfpConfig)
        );

        builder
            .Services.AddHttpClient<MyFitnessPalConnectorService>()
            .ConfigureMyFitnessPalClient();

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
        builder.Services.AddSingleton<
            IMyFitnessPalManualSyncService,
            MyFitnessPalManualSyncService
        >();
        builder.Services.AddHostedService<MyFitnessPalSyncService>();

        // Add health checks
        builder.Services.AddHealthChecks().AddCheck<MyFitnessPalHealthCheck>("myfitnesspal");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure manual sync endpoint
        app.MapPost(
            "/sync",
            async (IServiceProvider serviceProvider, CancellationToken cancellationToken) =>
            {
                var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

                try
                {
                    var manualSyncService =
                        serviceProvider.GetRequiredService<IMyFitnessPalManualSyncService>();

                    logger.LogInformation("Manual sync triggered for MyFitnessPal connector");
                    var success = await manualSyncService.TriggerManualSyncAsync(cancellationToken);

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

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting MyFitnessPal Connector Service...");

        await app.RunAsync();
    }
}
