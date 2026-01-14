using Microsoft.Extensions.Options;
using Nocturne.Connectors.Configurations;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Health;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.MyLife.Mappers;
using Nocturne.Connectors.MyLife.Services;

namespace Nocturne.Connectors.MyLife;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.AddServiceDefaults();

        var config = new MyLifeConnectorConfiguration();
        builder.Configuration.BindConnectorConfiguration(
            config,
            "MyLife",
            builder.Environment.ContentRootPath
        );

        builder.Services.AddSingleton<IOptions<MyLifeConnectorConfiguration>>(
            new OptionsWrapper<MyLifeConnectorConfiguration>(config)
        );
        builder.Services.AddSingleton(config);

        builder.Services.AddBaseConnectorServices();
        builder.Services.AddConnectorApiDataSubmitter(builder.Configuration);

        builder.Services.AddHttpClient<MyLifeSoapClient>();
        builder.Services.AddHttpClient<MyLifeAuthTokenProvider>();
        builder.Services.AddHttpClient<MyLifeConnectorService>();
        builder.Services.AddSingleton<MyLifeSessionStore>();
        builder.Services.AddSingleton<MyLifeAuthTokenProvider>();
        builder.Services.AddSingleton<IAuthTokenProvider>(sp =>
            sp.GetRequiredService<MyLifeAuthTokenProvider>());

        builder.Services.AddSingleton<MyLifeDecryptor>();
        builder.Services.AddSingleton<MyLifeArchiveReader>();
        builder.Services.AddSingleton<MyLifeSyncService>();
        builder.Services.AddSingleton<MyLifeEventProcessor>();
        builder.Services.AddSingleton<MyLifeEventsCache>();

        builder.Services.AddHostedService<MyLifeHostedService>();
        builder.Services.AddHealthChecks().AddConnectorHealthCheck("mylife");

        var app = builder.Build();

        app.MapDefaultEndpoints();
        app.MapConnectorEndpoints<MyLifeConnectorService, MyLifeConnectorConfiguration>("MyLife Connector");

        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Starting MyLife Connector Service...");

        await app.RunAsync();
    }
}
