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
using Nocturne.Connectors.MiniMed.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace Nocturne.Connectors.MiniMed;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add service defaults
        builder.AddServiceDefaults();

        // Configure services
        // Bind configuration for HttpClient setup
        var careLinkConfig = new CareLinkConnectorConfiguration();
        builder.Configuration.BindConnectorConfiguration(
            careLinkConfig,
            "MiniMed",
            builder.Environment.ContentRootPath
        );

        var server = careLinkConfig.CareLinkCountry.Equals("US", StringComparison.OrdinalIgnoreCase)
            ? "carelink.minimed.com"
            : "carelink.minimed.eu";

        // Register the fully bound configuration instance
        builder.Services.AddSingleton<IOptions<CareLinkConnectorConfiguration>>(
            new OptionsWrapper<CareLinkConnectorConfiguration>(careLinkConfig)
        );
        builder.Services.AddSingleton(careLinkConfig);

        builder.Services.AddHttpClient<CareLinkConnectorService>().ConfigureCareLinkClient(server);

        // Register the token provider for authentication
        builder.Services.AddHttpClient<CareLinkAuthTokenProvider>().ConfigureCareLinkClient(server);
        builder.Services.AddSingleton<IAuthTokenProvider>(sp =>
            sp.GetRequiredService<CareLinkAuthTokenProvider>());



        // Configure API data submitter for HTTP-based data submission
        builder.Services.AddConnectorApiDataSubmitter(builder.Configuration);
        builder.Services.AddHostedService<MiniMedHostedService>();

        // Add health checks
        // Add base connector services (State, Metrics, Strategies)
        builder.Services.AddBaseConnectorServices();
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("carelink");

        var app = builder.Build();

        // Map default endpoints (includes health checks in development)
        app.MapDefaultEndpoints();

        // Configure standard connector endpoints (Sync, Capabilities, Health/Data)
        app.MapConnectorEndpoints<CareLinkConnectorService, CareLinkConnectorConfiguration>("MiniMed Connector");

        // Configure graceful shutdown
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting MiniMed Connector Service...");

        await app.RunAsync();
    }
}
