using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
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

        // Configure services
        builder.Services.AddHttpClient();

        // Configure connector-specific services
        builder.Services.Configure<GlookoConnectorConfiguration>(
            builder.Configuration.GetSection("Connectors:Glooko")
        );

        // Configure API data submitter for HTTP-based data submission
        var apiUrl = builder.Configuration["NocturneApiUrl"];
        var apiSecret = builder.Configuration["ApiSecret"];

        builder.Services.AddSingleton<IApiDataSubmitter>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            var logger = sp.GetRequiredService<ILogger<ApiDataSubmitter>>();
            if (string.IsNullOrEmpty(apiUrl))
            {
                throw new InvalidOperationException("NocturneApiUrl configuration is missing.");
            }
            return new ApiDataSubmitter(httpClient, apiUrl, apiSecret, logger);
        });

        builder.Services.AddSingleton<GlookoConnectorService>(sp =>
        {
            var config = sp.GetRequiredService<IOptions<GlookoConnectorConfiguration>>().Value;
            var logger = sp.GetRequiredService<ILogger<GlookoConnectorService>>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient();
            var apiDataSubmitter = sp.GetRequiredService<IApiDataSubmitter>();

            return new GlookoConnectorService(config, logger, httpClient, apiDataSubmitter);
        });
        builder.Services.AddHostedService<GlookoHostedService>();

        // Add health checks
        builder.Services.AddHealthChecks().AddCheck<GlookoHealthCheck>("glooko");

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
                    .GetRequiredService<IOptionsSnapshot<GlookoConnectorConfiguration>>()
                    .Value;

                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var connectorService =
                        scope.ServiceProvider.GetRequiredService<GlookoConnectorService>();

                    logger.LogInformation("Manual sync triggered for Glooko connector");
                    var success = await connectorService.SyncGlookoHealthDataAsync(
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

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting Glooko Connector Service...");

        await app.RunAsync();
    }
}
