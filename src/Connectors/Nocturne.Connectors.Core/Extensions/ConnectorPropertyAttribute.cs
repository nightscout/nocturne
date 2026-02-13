namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Unified attribute for connector configuration properties.
///     Combines environment variable mapping, Aspire parameters, runtime configuration,
///     and validation into a single attribute.
/// </summary>
/// <remarks>
///     This attribute replaces the need for multiple attributes:
///     - [EnvironmentVariable]
///     - [AspireParameter]
///     - [RuntimeConfigurable]
///     - [Required]
///     - [Secret]
///     - [ConfigSchema]
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public class ConnectorPropertyAttribute : Attribute
{
    /// <summary>
    ///     Sentinel value indicating that MinValue or MaxValue is not set.
    /// </summary>
    private const int NotSet = int.MinValue;

    /// <summary>
    ///     Creates a new ConnectorPropertyAttribute with the configuration key name.
    /// </summary>
    /// <param name="configKey">
    ///     The configuration key name used in appsettings.json (e.g., "Username", "Password").
    ///     This is used for JSON binding and display purposes.
    /// </param>
    public ConnectorPropertyAttribute(string configKey)
    {
        ConfigKey = configKey;
    }

    /// <summary>
    ///     The configuration key name used in appsettings.json.
    ///     Example: "Username" for Parameters:Connectors:Dexcom:Username
    /// </summary>
    public string ConfigKey { get; }

    /// <summary>
    ///     The environment variable name suffix (without CONNECT_ prefix).
    ///     If not specified, derived from ConfigKey in SCREAMING_SNAKE_CASE.
    ///     Example: "USERNAME" becomes CONNECT_{CONNECTOR}_USERNAME
    /// </summary>
    public string? EnvVarSuffix { get; set; }

    /// <summary>
    ///     Whether this property is required. Defaults to false.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    ///     Whether this property contains sensitive data (passwords, tokens, etc.).
    ///     Secret properties are encrypted when stored and masked in logs.
    /// </summary>
    public bool Secret { get; set; }

    /// <summary>
    ///     Whether this property can be modified at runtime via the UI.
    ///     Defaults to false (static configuration only).
    /// </summary>
    public bool RuntimeConfigurable { get; set; }

    /// <summary>
    ///     Display name shown in UI. Defaults to ConfigKey if not specified.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    ///     Description shown in Aspire Dashboard and UI tooltips.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Category for grouping in UI (e.g., "Connection", "Sync", "Advanced").
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    ///     Default value if not specified in configuration.
    /// </summary>
    public string? DefaultValue { get; set; }

    /// <summary>
    ///     Allowed enum values for string properties.
    ///     Example: new[] { "US", "EU" }
    /// </summary>
    public string[]? AllowedValues { get; set; }

    /// <summary>
    ///     Minimum value for numeric properties. Use NotSet if not applicable.
    /// </summary>
    public int MinValue { get; set; } = NotSet;

    /// <summary>
    ///     Maximum value for numeric properties. Use NotSet if not applicable.
    /// </summary>
    public int MaxValue { get; set; } = NotSet;

    /// <summary>
    ///     Helper to check if MinValue is set.
    /// </summary>
    public bool HasMinValue => MinValue != NotSet;

    /// <summary>
    ///     Helper to check if MaxValue is set.
    /// </summary>
    public bool HasMaxValue => MaxValue != NotSet;

    /// <summary>
    ///     Format hint for string validation (e.g., "uri", "email").
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    ///     Gets the display name, falling back to ConfigKey.
    /// </summary>
    public string GetDisplayName() => DisplayName ?? ConfigKey;

    /// <summary>
    ///     Gets the environment variable suffix in SCREAMING_SNAKE_CASE.
    ///     Derives from ConfigKey if EnvVarSuffix is not explicitly set.
    /// </summary>
    public string GetEnvVarSuffix()
    {
        if (!string.IsNullOrEmpty(EnvVarSuffix))
            return EnvVarSuffix;

        // Convert ConfigKey to SCREAMING_SNAKE_CASE
        // e.g., "Username" -> "USERNAME", "PatientId" -> "PATIENT_ID"
        return ToScreamingSnakeCase(ConfigKey);
    }

    /// <summary>
    ///     Gets the full environment variable name given a connector prefix.
    /// </summary>
    /// <param name="connectorPrefix">The connector prefix (e.g., "DEXCOM", "LIBRE")</param>
    /// <returns>Full environment variable name (e.g., "CONNECT_DEXCOM_USERNAME")</returns>
    public string GetFullEnvVarName(string connectorPrefix)
    {
        return $"CONNECT_{connectorPrefix}_{GetEnvVarSuffix()}";
    }

    /// <summary>
    ///     Gets the Aspire parameter name given a connector prefix.
    /// </summary>
    /// <param name="connectorPrefix">The connector prefix in lowercase (e.g., "dexcom", "librelinkup")</param>
    /// <returns>Aspire parameter name (e.g., "dexcom-username")</returns>
    public string GetAspireParameterName(string connectorPrefix)
    {
        return $"{connectorPrefix}-{ConfigKey.ToLowerInvariant()}";
    }

    private static string ToScreamingSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = new System.Text.StringBuilder();
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c) && i > 0)
                result.Append('_');
            result.Append(char.ToUpperInvariant(c));
        }
        return result.ToString();
    }
}