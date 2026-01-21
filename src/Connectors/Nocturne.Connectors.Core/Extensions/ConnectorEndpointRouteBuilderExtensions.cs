using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;

namespace Nocturne.Connectors.Core.Extensions
{
    public static class ConnectorEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Maps standard connector endpoints including:
        /// - POST /sync
        /// - GET /capabilities
        /// - GET /health/data
        /// - GET /metrics (optional, TODO: standardize metrics endpoint structure if needed)
        /// </summary>
        /// <typeparam name="TService">The connector service type implementing IConnectorService<TConfig></typeparam>
        /// <typeparam name="TConfig">The connector configuration type implementing IConnectorConfiguration</typeparam>
        /// <param name="app">The WebApplication builder</param>
        /// <param name="connectorDisplayName">Display name for the connector (e.g. "Nightscout Connector")</param>
        /// <returns>The WebApplication for chaining</returns>
        public static WebApplication MapConnectorEndpoints<TService, TConfig>(
            this WebApplication app,
            string connectorDisplayName)
            where TService : class, IConnectorService<TConfig>
            where TConfig : class, IConnectorConfiguration
        {
            // Configure manual sync endpoint
            app.MapPost(
                "/sync",
                async (
                    [FromBody] SyncRequest request,
                    IServiceProvider serviceProvider,
                    CancellationToken cancellationToken
                ) =>
                {
                    // Use a generic logger for the endpoint or dynamic logger based on TService if preferred
                    // But typically Program logger or similar is used in original code.
                    // Let's use ILogger<TService> as it's more specific than Program.
                    var logger = serviceProvider.GetRequiredService<ILogger<TService>>();
                    var config = serviceProvider.GetRequiredService<TConfig>();

                    try
                    {
                        using var scope = serviceProvider.CreateScope();
                        var connectorService = scope.ServiceProvider.GetRequiredService<TService>();

                        logger.LogInformation(
                            "Manual sync triggered for {ConnectorName}. Request: {@Request}",
                            connectorDisplayName,
                            request
                        );

                        var result = await connectorService.SyncDataAsync(
                            request,
                            config,
                            cancellationToken
                        );

                        return Results.Ok(result);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error during manual sync for {ConnectorName}", connectorDisplayName);
                        return Results.Problem("Sync failed with error: " + ex.Message);
                    }
                }
            );

            // Configure capabilities endpoint
            app.MapGet(
                "/capabilities",
                (IServiceProvider serviceProvider) =>
                {
                    using var scope = serviceProvider.CreateScope();
                    var connectorService = scope.ServiceProvider.GetRequiredService<TService>();

                    return Results.Ok(new
                    {
                        supportedDataTypes = connectorService.SupportedDataTypes
                    });
                }
            );

            // Configure health/data endpoint
            app.MapGet(
                "/health/data",
                (IServiceProvider serviceProvider) =>
                {
                    var metricsTracker = serviceProvider.GetService<IConnectorMetricsTracker>();

                    // We need to resolve IOptionsSnapshot<TConfig> or TConfig directly.
                    // The original code used IOptionsSnapshot<TConfig> in Nightscout, but TConfig in Glooko/Dexcom.
                    // TConfig as a singleton (which they all seem to register) is safer if registered directly.
                    // Let's try to get TConfig directly since it's registered as singleton in all reviewed Programs.
                    var config = serviceProvider.GetRequiredService<TConfig>();

                    if (metricsTracker == null)
                    {
                        return Results.Ok(
                            new
                            {
                                connectorName = connectorDisplayName,
                                status = "running",
                                message = "Metrics tracking not available",
                            }
                        );
                    }

                    var recentTimestamps = metricsTracker.GetRecentEntryTimestamps(10);

                    return Results.Ok(
                        new
                        {
                            connectorName = connectorDisplayName,
                            status = "running",
                            metrics = new
                            {
                                totalEntries = metricsTracker.TotalEntries,
                                lastEntryTime = metricsTracker.LastEntryTime,
                                entriesLast24Hours = metricsTracker.EntriesLast24Hours,
                                lastSyncTime = metricsTracker.LastSyncTime,
                            },
                            recentEntries = recentTimestamps.Select(t => new { timestamp = t }).ToArray(),
                            configuration = new
                            {
                                syncIntervalMinutes = config.SyncIntervalMinutes,
                                connectSource = config.ConnectSource,
                            },
                        }
                    );
                }
            );

            // Configure effective configuration endpoint
            // This returns the current configuration values including those resolved from environment variables
            app.MapGet(
                "/config/effective",
                (IServiceProvider serviceProvider) =>
                {
                    var config = serviceProvider.GetRequiredService<TConfig>();
                    return Results.Ok(GetEffectiveConfiguration(config));
                }
            );

            return app;
        }

        /// <summary>
        /// Extracts the effective configuration values for properties marked with [RuntimeConfigurable].
        /// Returns only non-secret properties that can be displayed in the UI.
        /// </summary>
        private static Dictionary<string, object?> GetEffectiveConfiguration<TConfig>(TConfig config)
            where TConfig : class, IConnectorConfiguration
        {
            var result = new Dictionary<string, object?>();
            var configType = typeof(TConfig);

            foreach (var property in configType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var runtimeAttr = property.GetCustomAttribute<RuntimeConfigurableAttribute>();
                if (runtimeAttr == null)
                    continue; // Only include runtime-configurable properties

                var secretAttr = property.GetCustomAttribute<SecretAttribute>();
                if (secretAttr != null)
                    continue; // Don't include secrets

                try
                {
                    var value = property.GetValue(config);
                    // Convert enums to strings for JSON
                    if (value != null && property.PropertyType.IsEnum)
                    {
                        value = value.ToString();
                    }
                    result[ToCamelCase(property.Name)] = value;
                }
                catch
                {
                    // Skip properties that throw on access
                }
            }

            return result;
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }
    }
}
