using Nocturne.Connectors.Core.Models;
using System;

#nullable enable

namespace Nocturne.Connectors.Core.Extensions
{
    /// <summary>
    /// Marks a property as an Aspire parameter to be automatically registered in the Aspire Dashboard.
    /// Used by source generators to generate connector extension methods.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class AspireParameterAttribute : Attribute
    {
        /// <summary>
        /// The Aspire parameter name (e.g., "librelinkup-username")
        /// </summary>
        public string ParameterName { get; }

        /// <summary>
        /// The configuration path relative to Parameters:Connectors:{ConnectorName} (e.g., "Username")
        /// </summary>
        public string ConfigPath { get; }

        /// <summary>
        /// Whether this parameter contains sensitive data (passwords, tokens, etc.)
        /// </summary>
        public bool IsSecret { get; }

        /// <summary>
        /// Description shown in Aspire Dashboard
        /// </summary>
        public string? Description { get; }

        /// <summary>
        /// Default value if not specified in configuration
        /// </summary>
        public string? DefaultValue { get; }

        public AspireParameterAttribute(
            string parameterName,
            string configPath,
            bool secret = false,
            string? description = null,
            string? defaultValue = null
        )
        {
            ParameterName = parameterName;
            ConfigPath = configPath;
            IsSecret = secret;
            Description = description;
            DefaultValue = defaultValue;
        }
    }

    /// <summary>
    /// Marks a connector configuration class for automatic Aspire extension method generation.
    /// Used by source generators to create AddXxxConnector methods.
    /// Also provides display metadata for the connector in the UI.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class ConnectorRegistrationAttribute : Attribute
    {
        /// <summary>
        /// Connector name used in configuration paths (e.g., "LibreLinkUp")
        /// </summary>
        public string ConnectorName { get; }

        /// <summary>
        /// Project type name from generated Projects class (e.g., "Nocturne_Connectors_FreeStyle")
        /// </summary>
        public string ProjectTypeName { get; }

        /// <summary>
        /// Service name constant (e.g., "ServiceNames.LibreConnector")
        /// </summary>
        /// <example>ServiceNames.LibreConnector</example>
        public string ServiceName { get; }

        /// <summary>
        /// Environment variable prefix (e.g., "ServiceNames.ConnectorEnvironment.FreeStylePrefix")
        /// </summary>
        public string EnvironmentPrefix { get; }

        /// <summary>
        /// ConnectSource enum value (e.g., "ConnectSource.LibreLinkUp")
        /// </summary>
        public string ConnectSourceName { get; }

        /// <summary>
        /// The DataSources constant value used to identify data from this connector (e.g., "libre-connector")
        /// </summary>
        public string DataSourceId { get; }

        /// <summary>
        /// Icon identifier for the connector in the UI (e.g., "libre", "dexcom", "glooko")
        /// </summary>
        public string Icon { get; }

        /// <summary>
        /// Category for grouping in UI (e.g., "cgm", "pump", "data", "connector")
        /// </summary>
        public ConnectorCategory Category { get; }

        /// <summary>
        /// Human-readable description of the connector
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Human-readable display name for UI (e.g., "FreeStyle Libre").
        /// Falls back to ConnectorName if not specified.
        /// </summary>
        public string DisplayName { get; }

        /// <summary>
        /// Type of connector project (e.g., "CSharpProject", "PythonApp")
        /// </summary>
        public ConnectorType Type { get; }

        /// <summary>
        /// Path to script or app directory (referenced from solution root) for non-C# projects
        /// </summary>
        public string? ScriptPath { get; }

        public ConnectorRegistrationAttribute(
            string connectorName,
            string projectTypeName,
            string serviceName,
            string environmentPrefix,
            string connectSourceName,
            string dataSourceId = "",
            string icon = "",
            ConnectorCategory category = ConnectorCategory.Other,
            string description = "",
            string displayName = "",
            ConnectorType type = ConnectorType.CSharpProject,
            string? scriptPath = null
        )
        {
            ConnectorName = connectorName;
            ProjectTypeName = projectTypeName;
            ServiceName = serviceName;
            EnvironmentPrefix = environmentPrefix;
            ConnectSourceName = connectSourceName;
            DataSourceId = dataSourceId;
            Icon = icon;
            Category = category;
            Description = description;
            DisplayName = string.IsNullOrEmpty(displayName) ? connectorName : displayName;
            Type = type;
            ScriptPath = scriptPath;
        }
    }
}
