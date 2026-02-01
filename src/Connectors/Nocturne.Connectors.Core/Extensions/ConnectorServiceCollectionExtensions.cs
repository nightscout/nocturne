using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Options for configuring a connector via AddConnector
/// </summary>
public abstract class ConnectorOptions
{
    /// <summary>
    ///     The connector name used in configuration paths (e.g., "Dexcom", "LibreLinkUp")
    /// </summary>
    public required string ConnectorName { get; init; }

    /// <summary>
    ///     Server mapping for region-based server resolution.
    ///     Key: region code (e.g., "US", "EU"), Value: server URL
    /// </summary>
    public Dictionary<string, string>? ServerMapping { get; init; }

    /// <summary>
    ///     Default server URL if no region mapping matches
    /// </summary>
    public string? DefaultServer { get; init; }

    /// <summary>
    ///     Function to extract the server/region from the configuration
    /// </summary>
    public Func<BaseConnectorConfiguration, string>? GetServerRegion { get; init; }

    /// <summary>
    ///     Additional headers to include in HTTP requests
    /// </summary>
    public Dictionary<string, string>? AdditionalHeaders { get; init; }

    /// <summary>
    ///     Custom User-Agent string
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    ///     Request timeout
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    ///     Whether to add resilience policies (retry, circuit breaker)
    /// </summary>
    public bool AddResilience { get; init; }
}

public static class ConnectorServiceCollectionExtensions
{
    /// <param name="services">Service collection</param>
    extension(IServiceCollection services)
    {
        public IServiceCollection AddBaseConnectorServices()
        {
            // Default strategies
            services.TryAddSingleton<IRetryDelayStrategy, ProductionRetryDelayStrategy>();
            services.TryAddSingleton<IRateLimitingStrategy, ProductionRateLimitingStrategy>();

            // Treatment classification service for consistent bolus/carb classification
            services.TryAddSingleton<ITreatmentClassificationService, TreatmentClassificationService>();

            return services;
        }

        public TConfig AddConnectorConfiguration<TConfig>(IConfiguration configuration,
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

        /// <summary>
        ///     Registers a connector with its configuration, service, and token provider.
        ///     This is the preferred method for registering new connectors.
        /// </summary>
        /// <typeparam name="TConfig">Configuration type</typeparam>
        /// <typeparam name="TService">Connector service type</typeparam>
        /// <typeparam name="TTokenProvider">Token provider type</typeparam>
        /// <param name="configuration">Configuration</param>
        /// <param name="options">Connector options</param>
        /// <returns>The configuration if enabled, null otherwise</returns>
        public TConfig? AddConnector<TConfig, TService, TTokenProvider>(IConfiguration configuration,
            ConnectorOptions options)
            where TConfig : BaseConnectorConfiguration, new()
            where TService : class
            where TTokenProvider : class
        {
            // Register configuration
            var config = services.AddConnectorConfiguration<TConfig>(
                configuration,
                options.ConnectorName
            );

            // Skip registration if disabled
            if (!config.Enabled)
                return null;

            // Resolve server URL if mapping is provided
            string? serverUrl = null;
            if (options is { ServerMapping: not null, GetServerRegion: not null })
            {
                var region = options.GetServerRegion(config);
                serverUrl = ConnectorServerResolver.Resolve(
                    region,
                    options.ServerMapping,
                    options.DefaultServer ?? options.ServerMapping.Values.FirstOrDefault() ?? ""
                );
            }
            else if (options.DefaultServer != null)
            {
                serverUrl = options.DefaultServer;
            }

            // Register HttpClients with configuration
            if (serverUrl != null)
            {
                services.AddHttpClient<TService>()
                    .ConfigureConnectorClient(
                        serverUrl,
                        options.AdditionalHeaders,
                        options.UserAgent,
                        options.Timeout,
                        addResilience: options.AddResilience
                    );

                services.AddHttpClient<TTokenProvider>()
                    .ConfigureConnectorClient(
                        serverUrl,
                        options.AdditionalHeaders,
                        options.UserAgent,
                        options.Timeout,
                        addResilience: options.AddResilience
                    );
            }
            else
            {
                // Register without specific configuration
                services.AddHttpClient<TService>();
                services.AddHttpClient<TTokenProvider>();
            }

            return config;
        }

        /// <summary>
        ///     Simplified connector registration for connectors without token providers.
        /// </summary>
        public TConfig? AddConnector<TConfig, TService>(IConfiguration configuration,
            ConnectorOptions options)
            where TConfig : BaseConnectorConfiguration, new()
            where TService : class
        {
            // Register configuration
            var config = services.AddConnectorConfiguration<TConfig>(
                configuration,
                options.ConnectorName
            );

            // Skip registration if disabled
            if (!config.Enabled)
                return null;

            // Resolve server URL if mapping is provided
            string? serverUrl = null;
            if (options.ServerMapping != null && options.GetServerRegion != null)
            {
                var region = options.GetServerRegion(config);
                serverUrl = ConnectorServerResolver.Resolve(
                    region,
                    options.ServerMapping,
                    options.DefaultServer ?? options.ServerMapping.Values.FirstOrDefault() ?? ""
                );
            }
            else if (options.DefaultServer != null)
            {
                serverUrl = options.DefaultServer;
            }

            // Register HttpClient with configuration
            if (serverUrl != null)
            {
                services.AddHttpClient<TService>()
                    .ConfigureConnectorClient(
                        serverUrl,
                        options.AdditionalHeaders,
                        options.UserAgent,
                        options.Timeout,
                        addResilience: options.AddResilience
                    );
            }
            else
            {
                services.AddHttpClient<TService>();
            }

            return config;
        }
    }
}