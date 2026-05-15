using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
    ///     Connection timeout
    /// </summary>
    public TimeSpan? ConnectTimeout { get; init; }

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

            return services;
        }

        public TConfig AddConnectorConfiguration<TConfig>(IConfiguration configuration,
            string connectorName)
            where TConfig : BaseConnectorConfiguration, new()
        {
            var config = new TConfig();
            configuration.BindConnectorConfiguration(config, connectorName);

            // Register as frozen startup defaults (NOT as a DI service consumers inject)
            services.AddSingleton<IConnectorRegistration<TConfig>>(
                new ConnectorRegistration<TConfig>(config, connectorName));

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

            // Register server resolver
            services.AddSingleton<IConnectorServerResolver<TConfig>>(
                new ConnectorServerResolver<TConfig>(
                    options.ServerMapping,
                    options.GetServerRegion,
                    options.DefaultServer));

            // Register config loader
            services.AddSingleton<IConnectorConfigurationLoader<TConfig>, ConnectorConfigurationLoader<TConfig>>();

            // Register token cache (shared singleton across all connectors)
            services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
            services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

            // Register HttpClients WITHOUT BaseAddress (server resolved per-tenant at call time)
            services.AddHttpClient<TService>()
                .ConfigureConnectorClient(
                    null,
                    options.AdditionalHeaders,
                    options.UserAgent,
                    options.Timeout,
                    options.ConnectTimeout,
                    options.AddResilience
                );

            services.AddHttpClient<TTokenProvider>()
                .ConfigureConnectorClient(
                    null,
                    options.AdditionalHeaders,
                    options.UserAgent,
                    options.Timeout,
                    options.ConnectTimeout,
                    options.AddResilience
                );

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

            // Register server resolver
            services.AddSingleton<IConnectorServerResolver<TConfig>>(
                new ConnectorServerResolver<TConfig>(
                    options.ServerMapping,
                    options.GetServerRegion,
                    options.DefaultServer));

            // Register config loader
            services.AddSingleton<IConnectorConfigurationLoader<TConfig>, ConnectorConfigurationLoader<TConfig>>();

            // Register token cache (shared singleton across all connectors)
            services.TryAddSingleton<IConnectorTokenCache, ConnectorTokenCache>();
            services.TryAddSingleton<IConnectorCacheInvalidator>(sp => sp.GetRequiredService<IConnectorTokenCache>());

            // Register HttpClient WITHOUT BaseAddress (server resolved per-tenant at call time)
            services.AddHttpClient<TService>()
                .ConfigureConnectorClient(
                    null,
                    options.AdditionalHeaders,
                    options.UserAgent,
                    options.Timeout,
                    options.ConnectTimeout,
                    options.AddResilience
                );

            return config;
        }

        /// <summary>
        ///     Registers a token provider as a singleton, resolving the named HttpClient
        ///     from IHttpClientFactory and all other constructor dependencies from DI.
        ///     This replaces the manual factory lambda pattern used across connector installers.
        /// </summary>
        /// <typeparam name="TTokenProvider">Token provider type (must have a public constructor)</typeparam>
        public IServiceCollection AddConnectorTokenProvider<TTokenProvider>()
            where TTokenProvider : class
        {
            services.AddSingleton(sp =>
            {
                var factory = sp.GetRequiredService<IHttpClientFactory>();
                var httpClient = factory.CreateClient(typeof(TTokenProvider).Name);
                return ActivatorUtilities.CreateInstance<TTokenProvider>(sp, httpClient);
            });

            return services;
        }

        /// <summary>
        ///     Registers a sync executor as a scoped IConnectorSyncExecutor.
        /// </summary>
        /// <typeparam name="TSyncExecutor">Sync executor type</typeparam>
        public IServiceCollection AddConnectorSyncExecutor<TSyncExecutor>()
            where TSyncExecutor : class, IConnectorSyncExecutor
        {
            services.AddScoped<IConnectorSyncExecutor, TSyncExecutor>();
            return services;
        }

        /// <summary>
        ///     Discovers and registers all connector services via assembly scanning.
        ///     Replaces explicit per-connector AddXxxConnector() calls in Program.cs.
        /// </summary>
        /// <param name="configuration">Application configuration</param>
        /// <param name="backgroundServiceAssembly">
        ///     Optional assembly to scan for ConnectorBackgroundService implementations.
        ///     Typically the API assembly (typeof(Program).Assembly).
        /// </param>
        public IServiceCollection AddConnectors(
            IConfiguration configuration,
            Assembly? backgroundServiceAssembly = null)
        {
            // Connector assemblies may not be loaded yet since they're no longer
            // directly referenced in Program.cs. Load them from the app's base directory.
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (var dll in Directory.GetFiles(baseDir, "Nocturne.Connectors.*.dll"))
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(dll);
                    if (AppDomain.CurrentDomain.GetAssemblies()
                        .All(a => a.GetName().Name != assemblyName.Name))
                    {
                        Assembly.LoadFrom(dll);
                    }
                }
                catch
                {
                    // Skip assemblies that can't be loaded
                }
            }

            var connectorAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.FullName?.Contains("Nocturne.Connectors") == true)
                .ToList();

            // Discover and invoke all IConnectorInstaller implementations
            foreach (var assembly in connectorAssemblies)
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsAbstract || type.IsInterface)
                            continue;

                        if (!typeof(IConnectorInstaller).IsAssignableFrom(type))
                            continue;

                        var installer = (IConnectorInstaller)Activator.CreateInstance(type)!;
                        installer.Install(services, configuration);
                    }
                }
                catch (ReflectionTypeLoadException)
                {
                    // Some types may not be loadable, skip them
                }
            }

            // Auto-register background services
            if (backgroundServiceAssembly != null)
            {
                foreach (var type in backgroundServiceAssembly.GetTypes())
                {
                    if (type.IsAbstract || type.IsInterface)
                        continue;

                    // Check if the type extends ConnectorBackgroundService<TConfig>
                    var baseType = type.BaseType;
                    if (baseType is not { IsGenericType: true })
                        continue;

                    if (baseType.GetGenericTypeDefinition().Name != "ConnectorBackgroundService`1")
                        continue;

                    // Get TConfig type and check for ConnectorRegistrationAttribute
                    var configType = baseType.GetGenericArguments()[0];
                    var registration = configType.GetCustomAttribute<ConnectorRegistrationAttribute>();
                    if (registration == null)
                        continue;

                    // Check if the connector is enabled, using the same fallback
                    // chain as BindConnectorConfiguration: per-connector section
                    // → global Settings section → default (true)
                    var connectorName = registration.ConnectorName;
                    var section = configuration.GetSection($"Parameters:Connectors:{connectorName}");
                    if (!section.Exists())
                        section = configuration.GetSection($"Connectors:{connectorName}");

                    var isEnabled = section.GetValue<bool?>("Enabled")
                        ?? configuration.GetValue<bool?>("Parameters:Connectors:Settings:Enabled")
                        ?? configuration.GetValue<bool?>("Connectors:Settings:Enabled")
                        ?? true;

                    if (!isEnabled)
                        continue;

                    // Register the hosted service
                    var addHostedServiceMethod = typeof(ServiceCollectionHostedServiceExtensions)
                        .GetMethods()
                        .First(m => m.Name == "AddHostedService" && m.GetParameters().Length == 1)
                        .MakeGenericMethod(type);

                    addHostedServiceMethod.Invoke(null, [services]);
                }
            }

            return services;
        }
    }
}
