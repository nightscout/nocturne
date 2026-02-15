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

            SetPropertyValue(property, config, envValue);
        }
    }

    /// <summary>
    ///     Binds configuration section values to properties using [AspireParameter] or [ConnectorProperty] attributes.
    ///     This handles the mapping between JSON keys (e.g., "Username") and C# property names (e.g., "LibreUsername").
    /// </summary>
    private static void BindFromAttributes<T>(IConfigurationSection section, T config)
        where T : BaseConnectorConfiguration
    {
        if (!section.Exists())
            return;

        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            string? configKey = null;
            string? defaultValue = null;

            // Check for new [ConnectorProperty] attribute first
            var connectorProp = property.GetCustomAttribute<ConnectorPropertyAttribute>();
            if (connectorProp != null)
            {
                configKey = connectorProp.ConfigKey;
                defaultValue = connectorProp.DefaultValue;
            }
            else
            {
                // Fall back to legacy [AspireParameter] attribute
                var aspireAttr = property.GetCustomAttribute<AspireParameterAttribute>();
                if (aspireAttr != null)
                {
                    configKey = aspireAttr.ConfigPath;
                    defaultValue = aspireAttr.DefaultValue;
                }
            }

            if (configKey == null)
                continue;

            var configValue = section[configKey];

            // Use default value if config value is empty
            if (string.IsNullOrEmpty(configValue) && !string.IsNullOrEmpty(defaultValue))
                configValue = defaultValue;

            if (string.IsNullOrEmpty(configValue))
                continue;

            SetPropertyValue(property, config, configValue);
        }
    }

    /// <summary>
    ///     Binds environment variables to properties using [ConnectorProperty] attribute.
    /// </summary>
    private static void BindFromConnectorProperties<T>(IConfiguration configuration, T config, string connectorPrefix)
        where T : BaseConnectorConfiguration
    {
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var connectorProp = property.GetCustomAttribute<ConnectorPropertyAttribute>();
            if (connectorProp == null)
                continue;

            // Build full environment variable name: CONNECT_{PREFIX}_{SUFFIX}
            var envVarName = connectorProp.GetFullEnvVarName(connectorPrefix);
            var envValue = configuration[envVarName];

            if (string.IsNullOrEmpty(envValue))
                continue;

            SetPropertyValue(property, config, envValue);
        }
    }

    /// <summary>
    ///     Sets a property value with type conversion support.
    /// </summary>
    private static void SetPropertyValue(PropertyInfo property, object config, string value)
    {
        if (property.PropertyType == typeof(string))
        {
            property.SetValue(config, value);
        }
        else if (property.PropertyType == typeof(int))
        {
            if (int.TryParse(value, out var intValue))
                property.SetValue(config, intValue);
        }
        else if (property.PropertyType == typeof(bool))
        {
            if (bool.TryParse(value, out var boolValue))
                property.SetValue(config, boolValue);
        }
        else if (property.PropertyType == typeof(double))
        {
            if (double.TryParse(value, out var doubleValue))
                property.SetValue(config, doubleValue);
        }
    }

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
            // First use standard Bind for properties with matching names
            if (section.Exists()) section.Bind(config);

            // Then use [ConnectorProperty] or [AspireParameter] attributes for properties
            // with different names (e.g., JSON "Username" → C# property "LibreUsername")
            BindFromAttributes(section, config);

            // Override with environment variables (these take precedence)
            // Common base configuration properties
            if (bool.TryParse(configuration["Enabled"], out var enabled)) config.Enabled = enabled;

            if (int.TryParse(configuration["BatchSize"], out var batchSize)) config.BatchSize = batchSize;

            if (int.TryParse(configuration["SyncIntervalMinutes"], out var syncIntervalMinutes))
                config.SyncIntervalMinutes = syncIntervalMinutes;

            // Bind TimezoneOffset from environment variable (set by Aspire)
            if (double.TryParse(configuration["TimezoneOffset"], out var timezoneOffset))
                config.TimezoneOffset = timezoneOffset;

            // Derive connector prefix for environment variables (e.g., "DEXCOM", "LIBRE")
            var connectorPrefix = connectorName.ToUpperInvariant()
                .Replace("LINKUP", "")  // LibreLinkUp -> LIBRE
                .Replace(" ", "_");

            // Check dynamic environment variable: CONNECT_{CONNECTORNAME}_TIMEZONE_OFFSET
            var timezoneEnvVar = $"CONNECT_{connectorPrefix}_TIMEZONE_OFFSET";
            var timezoneEnvValue = configuration[timezoneEnvVar];
            if (!string.IsNullOrEmpty(timezoneEnvValue) && double.TryParse(timezoneEnvValue, out var envTimezoneOffset))
                config.TimezoneOffset = envTimezoneOffset;

            // Bind connector-specific environment variables using [ConnectorProperty] attribute
            BindFromConnectorProperties(configuration, config, connectorPrefix);

            // Also support legacy [EnvironmentVariable] attribute
            BindEnvironmentVariables(configuration, config);
        }
    }
}