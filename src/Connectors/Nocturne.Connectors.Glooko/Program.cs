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

        var server = glookoConfig.GlookoServer?.ToUpperInvariant() switch
        {
            "US" => GlookoConstants.Servers.US,
            "EU" => GlookoConstants.Servers.EU,
            _ => GlookoConstants.Configuration.DefaultServer,
        };

        builder.Services.AddHttpClient<GlookoConnectorService>().ConfigureGlookoClient(server);

        // Register strategies
        builder.Services.AddSingleton<IRetryDelayStrategy, ProductionRetryDelayStrategy>();
        builder.Services.AddSingleton<IRateLimitingStrategy, ProductionRateLimitingStrategy>();
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
