using Nocturne.Connectors.Core.Models;

namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Marks a property as an Aspire parameter to be automatically registered in the Aspire Dashboard.
///     Used by source generators to generate connector extension methods.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class AspireParameterAttribute(
    string parameterName,
    string configPath,
    bool secret = false,
    string? description = null,
    string? defaultValue = null)
    : Attribute
{
    /// <summary>
    ///     The Aspire parameter name (e.g., "librelinkup-username")
    /// </summary>
    public string ParameterName { get; } = parameterName;

    /// <summary>
    ///     The configuration path relative to Parameters:Connectors:{ConnectorName} (e.g., "Username")
    /// </summary>
    public string ConfigPath { get; } = configPath;

    /// <summary>
    ///     Whether this parameter contains sensitive data (passwords, tokens, etc.)
    /// </summary>
    public bool IsSecret { get; } = secret;

    /// <summary>
    ///     Description shown in Aspire Dashboard
    /// </summary>
    public string? Description { get; } = description;

    /// <summary>
    ///     Default value if not specified in configuration
    /// </summary>
    public string? DefaultValue { get; } = defaultValue;
}

/// <summary>
///     Marks a connector configuration class for automatic Aspire extension method generation.
///     Used by source generators to create AddXxxConnector methods.
///     Also provides display metadata for the connector in the UI.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class ConnectorRegistrationAttribute(
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
    string? scriptPath = null)
    : Attribute
{
    /// <summary>
    ///     Connector name used in configuration paths (e.g., "LibreLinkUp")
    /// </summary>
    public string ConnectorName { get; } = connectorName;

    /// <summary>
    ///     Project type name from generated Projects class (e.g., "Nocturne_Connectors_FreeStyle")
    /// </summary>
    public string ProjectTypeName { get; } = projectTypeName;

    /// <summary>
    ///     Service name constant (e.g., "ServiceNames.LibreConnector")
    /// </summary>
    /// <example>ServiceNames.LibreConnector</example>
    public string ServiceName { get; } = serviceName;

    /// <summary>
    ///     Environment variable prefix (e.g., "ServiceNames.ConnectorEnvironment.FreeStylePrefix")
    /// </summary>
    public string EnvironmentPrefix { get; } = environmentPrefix;

    /// <summary>
    ///     ConnectSource enum value (e.g., "ConnectSource.LibreLinkUp")
    /// </summary>
    public string ConnectSourceName { get; } = connectSourceName;

    /// <summary>
    ///     The DataSources constant value used to identify data from this connector (e.g., "libre-connector")
    /// </summary>
    public string DataSourceId { get; } = dataSourceId;

    /// <summary>
    ///     Icon identifier for the connector in the UI (e.g., "libre", "dexcom", "glooko")
    /// </summary>
    public string Icon { get; } = icon;

    /// <summary>
    ///     Category for grouping in UI (e.g., "cgm", "pump", "data", "connector")
    /// </summary>
    public ConnectorCategory Category { get; } = category;

    /// <summary>
    ///     Human-readable description of the connector
    /// </summary>
    public string Description { get; } = description;

    /// <summary>
    ///     Human-readable display name for UI (e.g., "FreeStyle Libre").
    ///     Falls back to ConnectorName if not specified.
    /// </summary>
    public string DisplayName { get; } = string.IsNullOrEmpty(displayName) ? connectorName : displayName;

    /// <summary>
    ///     Type of connector project (e.g., "CSharpProject", "PythonApp")
    /// </summary>
    public ConnectorType Type { get; } = type;

    /// <summary>
    ///     Path to script or app directory (referenced from solution root) for non-C# projects
    /// </summary>
    public string? ScriptPath { get; } = scriptPath;
}