namespace Nocturne.Connectors.Core.Extensions;

/// <summary>
///     Marks a property as runtime-configurable, meaning it can be stored in the database
///     and modified without restarting the service. Properties without this attribute
///     are considered static configuration (from environment variables or appsettings).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class RuntimeConfigurableAttribute : Attribute
{
    /// <summary>
    ///     Creates a new RuntimeConfigurableAttribute with default values.
    /// </summary>
    public RuntimeConfigurableAttribute()
    {
    }

    /// <summary>
    ///     Creates a new RuntimeConfigurableAttribute with a display name.
    /// </summary>
    public RuntimeConfigurableAttribute(string displayName)
    {
        DisplayName = displayName;
    }

    /// <summary>
    ///     Creates a new RuntimeConfigurableAttribute with display name and category.
    /// </summary>
    public RuntimeConfigurableAttribute(string displayName, string category)
    {
        DisplayName = displayName;
        Category = category;
    }

    /// <summary>
    ///     Display name shown in the UI configuration editor.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    ///     Description of what this configuration property does.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Category for grouping related settings in the UI.
    ///     Common categories: "General", "Connection", "Sync", "Advanced"
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    ///     Display order within the category (lower numbers appear first).
    /// </summary>
    public int Order { get; set; }
}

/// <summary>
///     Marks a property as a secret that should be encrypted when stored in the database.
///     Secret properties are never returned in plain text via API responses.
///     The encryption key is derived from the api-secret using PBKDF2.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class SecretAttribute : Attribute
{
}

/// <summary>
///     Specifies JSON Schema validation constraints for a configuration property.
///     These constraints are used for both server-side validation and generating
///     JSON Schema for client-side validation.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public class ConfigSchemaAttribute : Attribute
{
    private const int NotSet = int.MinValue;

    /// <summary>
    ///     Sentinel value indicating "not set" for numeric constraints.
    /// </summary>

    /// <summary>
    ///     Minimum value for numeric properties. Use NotSet (-2147483648) if not applicable.
    /// </summary>
    public int Minimum { get; set; } = NotSet;

    /// <summary>
    ///     Maximum value for numeric properties. Use NotSet (-2147483648) if not applicable.
    /// </summary>
    public int Maximum { get; set; } = NotSet;

    /// <summary>
    ///     Minimum length for string properties. Use NotSet (-2147483648) if not applicable.
    /// </summary>
    public int MinLength { get; set; } = NotSet;

    /// <summary>
    ///     Maximum length for string properties. Use NotSet (-2147483648) if not applicable.
    /// </summary>
    public int MaxLength { get; set; } = NotSet;

    /// <summary>
    ///     Regular expression pattern for string validation.
    /// </summary>
    public string? Pattern { get; set; }

    /// <summary>
    ///     Allowed values for enum-like string properties.
    /// </summary>
    public string[]? Enum { get; set; }

    /// <summary>
    ///     JSON Schema format hint (e.g., "uri", "email", "date-time", "hostname").
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    ///     Helper to check if Minimum is set.
    /// </summary>
    public bool HasMinimum => Minimum != NotSet;

    /// <summary>
    ///     Helper to check if Maximum is set.
    /// </summary>
    public bool HasMaximum => Maximum != NotSet;

    /// <summary>
    ///     Helper to check if MinLength is set.
    /// </summary>
    public bool HasMinLength => MinLength != NotSet;

    /// <summary>
    ///     Helper to check if MaxLength is set.
    /// </summary>
    public bool HasMaxLength => MaxLength != NotSet;
}
