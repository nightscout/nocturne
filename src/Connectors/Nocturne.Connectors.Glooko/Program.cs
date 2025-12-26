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
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

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
        builder.Services.AddSingleton(glookoConfig);

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

        // Register the token provider for authentication
        // Use a singleton instance with proper HttpClient configuration
        builder.Services.AddHttpClient<GlookoAuthTokenProvider>().ConfigureGlookoClient(server);
        builder.Services.AddSingleton(sp =>
        {
            // Create instance via HttpClientFactory to get properly configured HttpClient
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(GlookoAuthTokenProvider));
            var config = sp.GetRequiredService<IOptions<GlookoConnectorConfiguration>>();
            var logger = sp.GetRequiredService<ILogger<GlookoAuthTokenProvider>>();
            return new GlookoAuthTokenProvider(config, httpClient, logger);
        });
        builder.Services.AddSingleton<IAuthTokenProvider>(sp =>
            sp.GetRequiredService<GlookoAuthTokenProvider>());

        builder.Services.AddSingleton(
            typeof(IConnectorFileService<>),
            typeof(ConnectorFileService<>)
        );

        // Configure API data submitter for HTTP-based data submission
        builder.Services.AddConnectorApiDataSubmitter(builder.Configuration);
        builder.Services.AddHostedService<GlookoHostedService>();

        // Add health checks
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("glooko");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure standard connector endpoints (Sync, Capabilities, Health/Data)
        app.MapConnectorEndpoints<GlookoConnectorService, GlookoConnectorConfiguration>("Glooko Connector");

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting Glooko Connector Service...");

        await app.RunAsync();
    }
}
