using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.Core.Extensions;

public static class ConnectorServiceCollectionExtensions
{
    public static IServiceCollection AddBaseConnectorServices(this IServiceCollection services)
    {
        // Default strategies
        services.TryAddSingleton<IRetryDelayStrategy, ProductionRetryDelayStrategy>();
        services.TryAddSingleton<IRateLimitingStrategy, ProductionRateLimitingStrategy>();

        // Treatment classification service for consistent bolus/carb classification
        services.TryAddSingleton<ITreatmentClassificationService, TreatmentClassificationService>();

        return services;
    }

    public static TConfig AddConnectorConfiguration<TConfig>(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectorName)
        where TConfig : BaseConnectorConfiguration, new()
    {
        var config = new TConfig();
        configuration.BindConnectorConfiguration(config, connectorName);

        services.AddSingleton(config);
        services.AddSingleton<IOptions<TConfig>>(
            new OptionsWrapper<TConfig>(config)
        );

        return config;
    }

}
