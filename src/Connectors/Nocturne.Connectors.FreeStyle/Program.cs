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
using Nocturne.Connectors.FreeStyle.Constants;
using Nocturne.Connectors.FreeStyle.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Nocturne.Connectors.FreeStyle;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add service defaults
        builder.AddServiceDefaults();

        // Configure services
        // Bind configuration for HttpClient setup
        var libreConfig = new LibreLinkUpConnectorConfiguration();
        builder.Configuration.BindConnectorConfiguration(
            libreConfig,
            "FreeStyle",
            builder.Environment.ContentRootPath
        );

        // Register the fully bound configuration instance
        builder.Services.AddSingleton<IOptions<LibreLinkUpConnectorConfiguration>>(
            new OptionsWrapper<LibreLinkUpConnectorConfiguration>(libreConfig)
        );
        builder.Services.AddSingleton(libreConfig);

        // Map region to server endpoint
        var region =
            libreConfig.LibreRegion?.ToUpperInvariant()
            ?? LibreLinkUpConstants.Configuration.DefaultRegion;
        var server = region switch
        {
            "AE" => LibreLinkUpConstants.Endpoints.AE,
            "AP" => LibreLinkUpConstants.Endpoints.AP,
            "AU" => LibreLinkUpConstants.Endpoints.AU,
            "CA" => LibreLinkUpConstants.Endpoints.CA,
            "DE" => LibreLinkUpConstants.Endpoints.DE,
            "EU" => LibreLinkUpConstants.Endpoints.EU,
            "EU2" => LibreLinkUpConstants.Endpoints.EU2,
            "FR" => LibreLinkUpConstants.Endpoints.FR,
            "JP" => LibreLinkUpConstants.Endpoints.JP,
            "US" => LibreLinkUpConstants.Endpoints.US,
            _ => LibreLinkUpConstants.Endpoints.EU, // Default to EU if unknown
        };

        builder.Services.AddHttpClient<LibreConnectorService>().ConfigureLibreLinkUpClient(server);

        // Register the token provider for authentication
        builder.Services.AddHttpClient<LibreLinkAuthTokenProvider>().ConfigureLibreLinkUpClient(server);
        builder.Services.AddSingleton<IAuthTokenProvider>(sp =>
            sp.GetRequiredService<LibreLinkAuthTokenProvider>());

        // Configure API data submitter for HTTP-based data submission
        builder.Services.AddConnectorApiDataSubmitter(builder.Configuration);
        builder.Services.AddHostedService<FreeStyleHostedService>();

        // Add health checks
        // Add base connector services (State, Metrics, Strategies)
        builder.Services.AddBaseConnectorServices();
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("libre");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure standard connector endpoints (Sync, Capabilities, Health/Data)
        app.MapConnectorEndpoints<LibreConnectorService, LibreLinkUpConnectorConfiguration>("FreeStyle Connector");

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting FreeStyle Connector Service...");

        await app.RunAsync();
    }
}
