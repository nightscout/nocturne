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
public sealed class ConnectorOptions
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
        ///     Registers a connector with its configuration, service, token provider and sync
        ///     executor. This is the preferred method for registering new connectors.
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
            where TService : class, IConnectorService<TConfig>
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
            services.AddScoped<IConnectorConfigurationLoader<TConfig>, ConnectorConfigurationLoader<TConfig>>();

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

            services.AddConnectorTokenProvider<TTokenProvider>();
            services.AddConnectorSyncExecutor<ConnectorSyncExecutor<TService, TConfig>>();

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
            services.AddScoped(sp =>
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
        /// <exception cref="InvalidOperationException">
        ///     Another executor type already answers the same
        ///     <see cref="IConnectorSyncExecutor.ConnectorId"/>. A trigger resolves one executor per id
        ///     by enumeration order, so the collision would silently run one vendor's sync under the
        ///     other's trigger.
        /// </exception>
        public IServiceCollection AddConnectorSyncExecutor<TSyncExecutor>()
            where TSyncExecutor : class, IConnectorSyncExecutor, new()
        {
            var connectorId = new TSyncExecutor().ConnectorId;

            var clash = services.FirstOrDefault(descriptor =>
                descriptor.ServiceType == typeof(IConnectorSyncExecutor)
                && descriptor.ImplementationType is { } registered
                && registered != typeof(TSyncExecutor)
                && ConnectorIdOf(registered) == connectorId);

            if (clash is not null)
                throw new InvalidOperationException(
                    $"{typeof(TSyncExecutor).Name} and {clash.ImplementationType!.Name} both dispatch " +
                    $"on '{connectorId}'.");

            services.AddScoped<IConnectorSyncExecutor, TSyncExecutor>();
            return services;
        }

        /// <summary>
        ///     Discovers and registers all connector services via assembly scanning.
        ///     Replaces explicit per-connector AddXxxConnector() calls in Program.cs.
        /// </summary>
        /// <param name="configuration">Application configuration</param>
        /// <param name="pollingService">
        ///     Optional open generic hosted service over a connector's service and configuration
        ///     types — the API's <c>ConnectorBackgroundService&lt;TService, TConfig&gt;</c>.
        ///     Supplying it schedules every installed connector.
        /// </param>
        public IServiceCollection AddConnectors(
            IConfiguration configuration,
            Type? pollingService = null)
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

            if (pollingService is null)
                return services;

            void AddPollerIfEnabled(Type hostedService, Type configType)
            {
                // Per-connector section, then the global Settings section, then on by default.
                var connectorName = ConnectorRegistrationAttribute.DeclaredOn(configType).ConnectorName;
                var section = configuration.GetSection($"Parameters:Connectors:{connectorName}");
                if (!section.Exists())
                    section = configuration.GetSection($"Connectors:{connectorName}");

                var isEnabled = section.GetValue<bool?>("Enabled")
                    ?? configuration.GetValue<bool?>("Parameters:Connectors:Settings:Enabled")
                    ?? configuration.GetValue<bool?>("Connectors:Settings:Enabled")
                    ?? true;

                if (isEnabled)
                    services.TryAddEnumerable(
                        ServiceDescriptor.Singleton(typeof(IHostedService), hostedService));
            }

            // Connectors that need more than polling — a realtime listener — subclass the poller and
            // are registered as written; their configuration types then stand the generic down below.
            var scheduled = new HashSet<Type>();

            // The abstract half of the poller pair. A subclass that stops there is reached by neither
            // registration path, so it fails startup rather than leaving its connector polled by the
            // generic without the overrides it declares.
            var pollerBase = pollingService.BaseType is { IsGenericType: true } abstractBase
                ? abstractBase.GetGenericTypeDefinition()
                : null;

            foreach (var candidate in pollingService.Assembly.GetTypes())
            {
                if (candidate is not { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
                    continue;

                if (ClosedFormOf(candidate, pollingService) is not { } closed)
                {
                    if (pollerBase is not null && ClosedFormOf(candidate, pollerBase) is not null)
                        throw new InvalidOperationException(
                            $"{candidate.Name} derives from {pollerBase.Name} without closing " +
                            $"{pollingService.Name}, so no registration reaches it.");

                    continue;
                }

                var configType = closed.GetGenericArguments()[1];
                scheduled.Add(configType);
                AddPollerIfEnabled(candidate, configType);
            }

            var executors = services
                .Where(descriptor => descriptor.ServiceType == typeof(IConnectorSyncExecutor))
                .Select(descriptor => descriptor.ImplementationType)
                .ToList();

            foreach (var executor in executors)
            {
                var closed = ClosedFormOf(executor, typeof(ConnectorSyncExecutor<,>))
                    ?? throw new InvalidOperationException(
                        $"{executor?.Name ?? "A factory-registered sync executor"} does not derive from " +
                        "ConnectorSyncExecutor<TService, TConfig>, so the connector service and " +
                        "configuration types to poll cannot be read from it.");

                var arguments = closed.GetGenericArguments();
                if (!scheduled.Add(arguments[1]))
                    continue;

                AddPollerIfEnabled(pollingService.MakeGenericType(arguments), arguments[1]);
            }

            return services;
        }
    }

    /// <summary>
    ///     The closed <paramref name="openGeneric"/> in <paramref name="type"/>'s inheritance chain,
    ///     or <c>null</c> when it derives from no such thing.
    /// </summary>
    private static Type? ClosedFormOf(Type? type, Type openGeneric)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openGeneric)
                return current;

        return null;
    }

    private static string? ConnectorIdOf(Type executorType) =>
        executorType.GetConstructor(Type.EmptyTypes) is null
            ? null
            : ((IConnectorSyncExecutor)Activator.CreateInstance(executorType)!).ConnectorId;
}
