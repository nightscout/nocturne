using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.Core.Extensions
{
    public static class ConnectorServiceCollectionExtensions
    {
        public static IServiceCollection AddBaseConnectorServices(this IServiceCollection services)
        {
            // Core state and metrics services
            services.TryAddSingleton<IConnectorStateService, ConnectorStateService>();
            services.TryAddSingleton<IConnectorMetricsTracker, ConnectorMetricsTracker>();

            // Default strategies
            services.TryAddSingleton<IRetryDelayStrategy, ProductionRetryDelayStrategy>();
            services.TryAddSingleton<IRateLimitingStrategy, ProductionRateLimitingStrategy>();

            return services;
        }
    }
}
