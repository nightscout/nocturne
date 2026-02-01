using System.Reflection;
using Microsoft.Extensions.Configuration;
using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Attribute to map configuration properties to environment variables
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class EnvironmentVariableAttribute(string name) : Attribute
{
    /// <summary>
    ///     The environment variable name to bind from
    /// </summary>
    public string Name { get; } = name;
}

/// <summary>
///     Extension methods for simplified connector configuration binding
/// </summary>
public static class ConfigurationExtensions
{
    /// <param name="configuration">The IConfiguration instance</param>
    extension(IConfiguration configuration)
    {
        /// <summary>
        ///     Binds connector configuration from multiple sources in order of precedence:
        ///     1. Environment variables (highest priority)
        ///     2. Parameters:Connectors:{ConnectorName} section
        ///     3. Connectors:{ConnectorName} section (fallback)
        /// </summary>
        /// <typeparam name="T">The configuration type</typeparam>
        /// <param name="config">The configuration object to bind to</param>
        /// <param name="connectorName">The connector name (e.g., "Glooko", "Dexcom")</param>
        public void BindConnectorConfiguration<T>(T config, string connectorName)
            where T : BaseConnectorConfiguration
        {
            // 1. Bind Global Settings (Parameters:Connectors:Settings)
            var globalSection = configuration.GetSection("Parameters:Connectors:Settings");
            if (globalSection.Exists()) globalSection.Bind(config);

            // 2. Bind Specific Connector Settings
            // Try primary configuration path
            var section = configuration.GetSection($"Parameters:Connectors:{connectorName}");

            // Fallback to alternate path if not found
            if (!section.Exists()) section = configuration.GetSection($"Connectors:{connectorName}");

            // Bind the section if it exists
            if (section.Exists()) section.Bind(config);

            // Override with environment variables (these take precedence)
            // Common base configuration properties
            if (bool.TryParse(configuration["Enabled"], out var enabled)) config.Enabled = enabled;

            if (int.TryParse(configuration["BatchSize"], out var batchSize)) config.BatchSize = batchSize;

            if (int.TryParse(configuration["SyncIntervalMinutes"], out var syncIntervalMinutes))
                config.SyncIntervalMinutes = syncIntervalMinutes;

            // Bind TimezoneOffset from environment variable (set by Aspire)
            if (double.TryParse(configuration["TimezoneOffset"], out var timezoneOffset))
                config.TimezoneOffset = timezoneOffset;

            // Also check dynamic environment variable: CONNECT_{CONNECTORNAME}_TIMEZONE_OFFSET
            // This takes precedence over the simple TimezoneOffset env var
            var timezoneEnvVar = $"CONNECT_{connectorName.ToUpperInvariant()}_TIMEZONE_OFFSET";
            var timezoneEnvValue = configuration[timezoneEnvVar];
            if (!string.IsNullOrEmpty(timezoneEnvValue) && double.TryParse(timezoneEnvValue, out var envTimezoneOffset))
                config.TimezoneOffset = envTimezoneOffset;

            // Bind connector-specific environment variables using reflection
            BindEnvironmentVariables(configuration, config);
        }
    }

    /// <summary>
    ///     Binds environment variables to properties marked with [EnvironmentVariable] attribute
    /// </summary>
    private static void BindEnvironmentVariables<T>(IConfiguration configuration, T config)
        where T : BaseConnectorConfiguration
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var envVarAttribute = property.GetCustomAttribute<EnvironmentVariableAttribute>();
            if (envVarAttribute == null)
                continue;

            var envValue = configuration[envVarAttribute.Name];
            if (string.IsNullOrEmpty(envValue))
                continue;

            // Handle different property types
            if (property.PropertyType == typeof(string))
            {
                property.SetValue(config, envValue);
            }
            else if (property.PropertyType == typeof(int))
            {
                if (int.TryParse(envValue, out var intValue)) property.SetValue(config, intValue);
            }
            else if (property.PropertyType == typeof(bool))
            {
                if (bool.TryParse(envValue, out var boolValue)) property.SetValue(config, boolValue);
            }
        }
    }
}
