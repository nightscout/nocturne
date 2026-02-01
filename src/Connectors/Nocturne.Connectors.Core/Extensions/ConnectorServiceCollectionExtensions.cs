using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;

namespace Nocturne.Connectors.Core.Extensions
{
    public static class ConnectorServiceCollectionExtensions
    {
        /// <summary>
        /// Checks if this connector is enabled and logs appropriately.
        /// Call at the start of Main() and return early if false.
        /// This supports the runtime configuration model where all connectors are deployed
        /// but self-disable based on environment variables.
        /// </summary>
        /// <param name="configuration">Application configuration</param>
        /// <param name="connectorName">Name of the connector (e.g., "Dexcom", "Glooko")</param>
        /// <returns>True if connector should run, false if it should exit</returns>
        /// <example>
        /// if (!builder.Configuration.IsConnectorEnabled("Dexcom")) return;
        /// </example>
        public static bool IsConnectorEnabled(this IConfiguration configuration, string connectorName)
        {
            // Check the standard config path: Parameters:Connectors:{Name}:Enabled
            // Environment variable form: Parameters__Connectors__{Name}__Enabled
            var enabled = configuration.GetValue<bool>($"Parameters:Connectors:{connectorName}:Enabled", false);
            if (!enabled)
            {
                Console.WriteLine($"[{connectorName}] Connector not enabled. Exiting.");
            }
            return enabled;
        }

        public static IServiceCollection AddBaseConnectorServices(this IServiceCollection services)
        {
            // Core state and metrics services
            services.TryAddSingleton<IConnectorStateService, ConnectorStateService>();
            services.TryAddSingleton<IConnectorMetricsTracker, ConnectorMetricsTracker>();

            // Default strategies
            services.TryAddSingleton<IRetryDelayStrategy, ProductionRetryDelayStrategy>();
            services.TryAddSingleton<IRateLimitingStrategy, ProductionRateLimitingStrategy>();

            // Treatment classification service for consistent bolus/carb classification
            services.TryAddSingleton<ITreatmentClassificationService, TreatmentClassificationService>();

            return services;
        }

        public static IServiceCollection AddConnectorApiDataSubmitter(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            var apiUrl = configuration["NocturneApiUrl"];
            var apiSecret = configuration["ApiSecret"];

            services.AddSingleton<IApiDataSubmitter>(sp =>
            {
                var httpClientFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient("NocturneApi");
                var logger = sp.GetRequiredService<ILogger<ApiDataSubmitter>>();
                if (string.IsNullOrEmpty(apiUrl))
                {
                    throw new InvalidOperationException("NocturneApiUrl configuration is missing.");
                }
                return new ApiDataSubmitter(httpClient, apiUrl, apiSecret, logger);
            });

            return services;
        }

        /// <summary>
        /// Adds the configuration client for fetching runtime configuration from the API.
        /// </summary>
        /// <param name="services">The service collection</param>
        /// <param name="configuration">Application configuration</param>
        /// <returns>The service collection for chaining</returns>
        public static IServiceCollection AddConfigurationClient(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            var apiUrl = configuration["NocturneApiUrl"]
                      ?? configuration["services:nocturne-api:https:0"]
                      ?? configuration["services:nocturne-api:http:0"];

            if (string.IsNullOrEmpty(apiUrl))
            {
                throw new InvalidOperationException("NocturneApiUrl configuration is missing. Set NocturneApiUrl or use Aspire service discovery.");
            }

            services.AddSingleton<IConfigurationClient>(sp =>
            {
                var httpClientFactory = sp.GetRequiredService<System.Net.Http.IHttpClientFactory>();
                var httpClient = httpClientFactory.CreateClient("ConfigurationClient");
                var logger = sp.GetRequiredService<ILogger<ConfigurationClient>>();
                return new ConfigurationClient(httpClient, apiUrl, logger);
            });

            return services;
        }
    }
}
