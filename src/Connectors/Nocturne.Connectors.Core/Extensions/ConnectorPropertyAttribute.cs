namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Unified attribute for connector configuration properties.
///     Combines environment variable mapping, Aspire parameters, and validation
///     into a single attribute.
/// </summary>
/// <remarks>
///     This attribute replaces the need for multiple attributes:
///     - [EnvironmentVariable]
///     - [AspireParameter]
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
    ///     Creates a new ConnectorPropertyAttribute with the configuration key.
    /// </summary>
    /// <param name="key">
    ///     The configuration key used for identification and JSON binding.
    /// </param>
    public ConnectorPropertyAttribute(ConnectorPropertyKey key)
    {
        Key = key;
    }

    /// <summary>
    ///     The configuration key enum value.
    /// </summary>
    public ConnectorPropertyKey Key { get; }

    /// <summary>
    ///     The environment variable name suffix (without CONNECT_ prefix).
    ///     If not specified, derived from Key enum name in SCREAMING_SNAKE_CASE.
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
    ///     Whether this property is hidden from the configuration UI. Hidden properties are still
    ///     bound from config and usable as an advanced override, but are not rendered as a form
    ///     field — used for values the connector derives automatically (e.g. an auto-discovered id).
    /// </summary>
    public bool Hidden { get; set; }

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
    ///     Gets the property key name as a string (enum name).
    /// </summary>
    public string GetKeyName() => Key.ToString();

    /// <summary>
    ///     Gets the environment variable suffix in SCREAMING_SNAKE_CASE.
    ///     Derives from Key enum name if EnvVarSuffix is not explicitly set.
    /// </summary>
    public string GetEnvVarSuffix()
    {
        if (!string.IsNullOrEmpty(EnvVarSuffix))
            return EnvVarSuffix;

        // Convert Key enum name to SCREAMING_SNAKE_CASE
        // e.g., "Username" -> "USERNAME", "PatientId" -> "PATIENT_ID"
        return ToScreamingSnakeCase(Key.ToString());
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
        return $"{connectorPrefix}-{Key.ToString().ToLowerInvariant()}";
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
